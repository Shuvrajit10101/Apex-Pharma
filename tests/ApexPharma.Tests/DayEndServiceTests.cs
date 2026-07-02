using System;
using System.Linq;
using System.Threading.Tasks;
using ApexPharma.Application.Services;
using ApexPharma.Application.Services.DayEnd;
using ApexPharma.Application.Services.Ledger;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ApexPharma.Tests;

/// <summary>
/// DayEndService tests (plan.md §3, §11 — Phase 2e) — day-end cash reconciliation + the Cashier's
/// own-day view. Cover: the expected-cash math (Float + cashSales + cashReceipts − cashRefunds −
/// cashSupplierPayments, with non-cash sales/receipts/payments excluded); variance sign; the day
/// window boundary (23:59:59 in, next-day 00:00 out) in explicit UTC; the refund parent-sale-mode
/// proxy (cash parent reduces expected, credit parent does not); Cashier CreatedBy scoping; an empty
/// day (expected == float); close snapshotting + server-side recompute (a wrong UI expected is
/// ignored); one-close-per-day; variance-requires-note; snapshot immutability against later
/// back-dated cash; opening-float carry-forward; RBAC (including the Cashier close rejection and
/// summary coercion); and ACID rollback on a forced mid-close failure.
/// </summary>
public class DayEndServiceTests : IDisposable
{
    private readonly SqliteInMemoryContext _fixture = new();
    private readonly BillingService _billing;
    private readonly SaleReturnService _returns;
    private readonly CustomerLedgerService _customerLedger;
    private readonly SupplierLedgerService _supplierLedger;
    private readonly DayEndService _sut;

    private int _ownerId;
    private int _cashierAId;
    private int _cashierBId;
    private int _supplierId;
    private int _catId;
    private int _manId;

    public DayEndServiceTests()
    {
        var auth = new AuthService(_fixture.Context);
        var gst = new GstService();
        _billing = new BillingService(_fixture.Context, auth, gst);
        _returns = new SaleReturnService(_fixture.Context, auth);
        _customerLedger = new CustomerLedgerService(_fixture.Context, auth);
        _supplierLedger = new SupplierLedgerService(_fixture.Context, auth);
        _sut = new DayEndService(_fixture.Context, auth);
        Seed();
    }

    public void Dispose() => _fixture.Dispose();

    private void Seed()
    {
        var db = _fixture.Context;
        var role = new Role { Name = "Owner" };
        db.Roles.Add(role);
        db.SaveChanges();

        var owner = new User { Username = "owner", PasswordHash = "x", FullName = "Owner", RoleId = role.RoleId };
        var cashierA = new User { Username = "cashierA", PasswordHash = "x", FullName = "Cashier A", RoleId = role.RoleId };
        var cashierB = new User { Username = "cashierB", PasswordHash = "x", FullName = "Cashier B", RoleId = role.RoleId };
        db.Users.AddRange(owner, cashierA, cashierB);

        var cat = new Category { Name = "Medication" };
        var man = new Manufacturer { Name = "Cipla" };
        var supplier = new Supplier { Name = "MediDist", IsActive = true };
        db.Categories.Add(cat);
        db.Manufacturers.Add(man);
        db.Suppliers.Add(supplier);
        db.SaveChanges();

        _ownerId = owner.UserId;
        _cashierAId = cashierA.UserId;
        _cashierBId = cashierB.UserId;
        _supplierId = supplier.SupplierId;
        _catId = cat.CategoryId;
        _manId = man.ManufacturerId;
    }

    private Product AddProduct(string name, decimal gstRate = 0m)
    {
        var db = _fixture.Context;
        var p = new Product { Name = name, CategoryId = _catId, ManufacturerId = _manId, GstRate = gstRate, IsActive = true, ReorderLevel = 0 };
        db.Products.Add(p);
        db.SaveChanges();
        return p;
    }

    private Batch AddBatch(int productId, string batchNo, decimal qty, decimal salePrice)
    {
        var db = _fixture.Context;
        var b = new Batch
        {
            ProductId = productId,
            BatchNo = batchNo,
            ExpiryDate = DateTime.UtcNow.Date.AddYears(1),
            Mrp = salePrice,
            PurchasePrice = salePrice,
            SalePrice = salePrice,
            QtyOnHand = qty,
            SupplierId = _supplierId,
            ReceivedDate = DateTime.UtcNow,
        };
        db.Batches.Add(b);
        db.SaveChanges();
        return b;
    }

    private int AddCustomer(string name = "Ravi", decimal balance = 0m)
    {
        var db = _fixture.Context;
        var c = new Customer { Name = name, CreditLimit = 1_000_000m, Balance = balance };
        db.Customers.Add(c);
        db.SaveChanges();
        return c.CustomerId;
    }

    /// <summary>Creates a sale of the given mode + qty by the given user, then stamps its BillDate to a fixed UTC instant.</summary>
    private async Task<(string BillNo, int SaleId, int SaleItemId, decimal Total)> SaleAtAsync(
        PaymentMode mode, int productId, decimal qty, int userId, DateTime billDateUtc, int? customerId = null)
    {
        var input = new SaleInput
        {
            PaymentMode = mode,
            CustomerId = customerId,
            Lines = { new SaleLineInput { ProductId = productId, Qty = qty } },
        };
        var result = await _billing.CreateSaleAsync(input, UserRole.Owner, userId);
        Assert.True(result.Succeeded, result.Error);

        var live = _fixture.Context;
        Sale sale = await live.Sales.OrderByDescending(s => s.SaleId).FirstAsync();
        sale.BillDate = billDateUtc;
        await live.SaveChangesAsync();

        int saleItemId = (await _fixture.NewContext().SaleItems.OrderByDescending(i => i.SaleItemId).FirstAsync()).SaleItemId;
        return (result.Value!.BillNo, sale.SaleId, saleItemId, result.Value.Total);
    }

    private async Task StampReturnDateAsync(DateTime dateUtc)
    {
        var live = _fixture.Context;
        SaleReturn r = await live.SaleReturns.OrderByDescending(x => x.ReturnId).FirstAsync();
        r.Date = dateUtc;
        await live.SaveChangesAsync();
    }

    private async Task StampReceiptDateAsync(DateTime dateUtc)
    {
        var live = _fixture.Context;
        CustomerReceipt r = await live.CustomerReceipts.OrderByDescending(x => x.CustomerReceiptId).FirstAsync();
        r.ReceiptDate = dateUtc;
        await live.SaveChangesAsync();
    }

    private async Task StampSupplierPaymentDateAsync(DateTime dateUtc)
    {
        var live = _fixture.Context;
        SupplierPayment p = await live.SupplierPayments.OrderByDescending(x => x.SupplierPaymentId).FirstAsync();
        p.PaymentDate = dateUtc;
        await live.SaveChangesAsync();
    }

    private static readonly DateTime Day = new(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);
    private static DateTime At(int hour, int minute = 0, int second = 0) =>
        new(2026, 6, 15, hour, minute, second, DateTimeKind.Utc);

    // ---- 1. Expected math + non-cash exclusion ----

    [Fact]
    public async Task Expected_Is_Float_Plus_CashSales_Plus_CashReceipts_Minus_Refunds_Minus_SupplierPayments()
    {
        // GST 0% keeps money round: a sale of 10 @ 100 = 1000 total.
        var p = AddProduct("Paracetamol", gstRate: 0m);
        AddBatch(p.ProductId, "B1", qty: 10000m, salePrice: 100m);
        int custId = AddCustomer(balance: 100_000m);

        // Cash sale 1000 (in). A UPI sale 500 and a Credit sale 700 must be EXCLUDED from expected.
        await SaleAtAsync(PaymentMode.Cash, p.ProductId, 10m, _ownerId, At(10));
        await SaleAtAsync(PaymentMode.Upi, p.ProductId, 5m, _ownerId, At(11));
        await SaleAtAsync(PaymentMode.Credit, p.ProductId, 7m, _ownerId, At(12), customerId: custId);

        // Cash receipt 300 (in) + a UPI receipt 200 (excluded).
        await _customerLedger.RecordReceiptAsync(new CustomerReceiptInput(custId, 300m, PaymentMode.Cash), _ownerId, UserRole.Owner);
        await StampReceiptDateAsync(At(13));
        await _customerLedger.RecordReceiptAsync(new CustomerReceiptInput(custId, 200m, PaymentMode.Upi), _ownerId, UserRole.Owner);
        await StampReceiptDateAsync(At(14));

        // Supplier payment: seed a purchase so a payable exists, then pay 250 cash (out) + 150 UPI (excluded).
        await SeedPayableAsync(2000m);
        await _supplierLedger.RecordPaymentAsync(new SupplierPaymentInput(_supplierId, 250m, PaymentMode.Cash), _ownerId, UserRole.Owner);
        await StampSupplierPaymentDateAsync(At(15));
        await _supplierLedger.RecordPaymentAsync(new SupplierPaymentInput(_supplierId, 150m, PaymentMode.Upi), _ownerId, UserRole.Owner);
        await StampSupplierPaymentDateAsync(At(16));

        var res = await _sut.GetDaySummaryAsync(Day, _ownerId, UserRole.Owner, scopedToUserId: null);
        Assert.True(res.Succeeded, res.Error);
        var s = res.Value!;

        Assert.Equal(0m, s.OpeningFloat);
        Assert.Equal(1000m, s.CashSales);
        Assert.Equal(300m, s.CashReceipts);
        Assert.Equal(0m, s.CashRefunds);
        Assert.Equal(250m, s.CashSupplierPayments);
        // 0 + 1000 + 300 − 0 − 250 = 1050.
        Assert.Equal(1050m, s.ExpectedCash);

        // Non-cash tiles reflect the other modes; UPI/Credit are NOT in expected.
        Assert.Equal(500m, s.UpiTotal);
        Assert.Equal(700m, s.CreditTotal);
        Assert.Equal(0m, s.CardTotal);
        Assert.Equal(3, s.BillCount);
        Assert.Equal(2200m, s.GrossSales); // 1000 + 500 + 700
    }

    /// <summary>Seeds a purchase (creating a payable) so a supplier payment can be recorded.</summary>
    private async Task SeedPayableAsync(decimal amount)
    {
        var db = _fixture.Context;
        var purchase = new Purchase
        {
            SupplierId = _supplierId,
            InvoiceDate = DateTime.UtcNow,
            SupplierInvoiceNo = "PINV-1",
            Subtotal = amount,
            Total = amount,
            CreatedBy = _ownerId,
        };
        db.Purchases.Add(purchase);
        await db.SaveChangesAsync();
    }

    // ---- 2. Variance sign ----

    [Fact]
    public async Task Variance_Over_Short_Zero()
    {
        var p = AddProduct("Paracetamol");
        AddBatch(p.ProductId, "B1", qty: 10000m, salePrice: 100m);
        await SaleAtAsync(PaymentMode.Cash, p.ProductId, 10m, _ownerId, At(10)); // expected = 1000

        // Over: counted 1100 → +100.
        var over = await _sut.CloseDayAsync(new DayEndCloseInput(Day, 0m, 1100m, Note: "extra float"), _ownerId, UserRole.Owner);
        Assert.True(over.Succeeded, over.Error);
        Assert.Equal(100m, over.Value!.Variance);
    }

    [Fact]
    public async Task Variance_Short_And_Zero()
    {
        var p = AddProduct("Paracetamol");
        AddBatch(p.ProductId, "B1", qty: 10000m, salePrice: 100m);
        await SaleAtAsync(PaymentMode.Cash, p.ProductId, 10m, _ownerId, At(10)); // expected = 1000

        // Zero variance (no note required).
        var zero = await _sut.CloseDayAsync(new DayEndCloseInput(Day, 0m, 1000m), _ownerId, UserRole.Owner);
        Assert.True(zero.Succeeded, zero.Error);
        Assert.Equal(0m, zero.Value!.Variance);

        // A different day, short: counted 900 → −100.
        DateTime day2 = Day.AddDays(1);
        await SaleAtAsync(PaymentMode.Cash, p.ProductId, 10m, _ownerId, day2.AddHours(10));
        var shortClose = await _sut.CloseDayAsync(new DayEndCloseInput(day2, 1000m, 1900m, Note: "till short"), _ownerId, UserRole.Owner);
        Assert.True(shortClose.Succeeded, shortClose.Error);
        // Day2 opening float carries forward from day1's carry (= counted 1000). Expected = 1000 + 1000 = 2000; counted 1900 → −100.
        Assert.Equal(-100m, shortClose.Value!.Variance);
    }

    // ---- 3. Day-window boundary ----

    [Fact]
    public async Task DayWindow_Boundary_LastSecondIn_NextMidnightOut()
    {
        var p = AddProduct("Paracetamol");
        AddBatch(p.ProductId, "B1", qty: 10000m, salePrice: 100m);

        // 23:59:59 same day → IN.
        await SaleAtAsync(PaymentMode.Cash, p.ProductId, 10m, _ownerId, At(23, 59, 59)); // 1000
        // 00:00:00 next day → OUT.
        await SaleAtAsync(PaymentMode.Cash, p.ProductId, 5m, _ownerId, Day.AddDays(1)); // 500 next day

        var res = await _sut.GetDaySummaryAsync(Day, _ownerId, UserRole.Owner, scopedToUserId: null);
        Assert.True(res.Succeeded);
        Assert.Equal(1000m, res.Value!.CashSales); // only the last-second sale
        Assert.Equal(1, res.Value.BillCount);
    }

    // ---- 4. Refund parent-sale-mode proxy ----

    [Fact]
    public async Task Refund_Against_CashParent_Reduces_Expected_CreditParent_DoesNot()
    {
        var p = AddProduct("Paracetamol");
        AddBatch(p.ProductId, "B1", qty: 10000m, salePrice: 100m);
        int custId = AddCustomer(balance: 100_000m);

        // Cash sale of 10 @ 100 = 1000; refund 3 → 300 refund (cash-out).
        var cashSale = await SaleAtAsync(PaymentMode.Cash, p.ProductId, 10m, _ownerId, At(10));
        var cashRet = await _returns.ProcessSaleReturnAsync(
            new SaleReturnInput { BillNo = cashSale.BillNo, Lines = { new SaleReturnLineInput { SaleItemId = cashSale.SaleItemId, Qty = 3m } } },
            _ownerId, UserRole.Owner);
        Assert.True(cashRet.Succeeded, cashRet.Error);
        await StampReturnDateAsync(At(11));

        // Credit sale of 10 @ 100 = 1000; refund 4 → 400 reversal, but parent is CREDIT so NOT cash-out.
        var creditSale = await SaleAtAsync(PaymentMode.Credit, p.ProductId, 10m, _ownerId, At(12), customerId: custId);
        var creditRet = await _returns.ProcessSaleReturnAsync(
            new SaleReturnInput { BillNo = creditSale.BillNo, Lines = { new SaleReturnLineInput { SaleItemId = creditSale.SaleItemId, Qty = 4m } } },
            _ownerId, UserRole.Owner);
        Assert.True(creditRet.Succeeded, creditRet.Error);
        await StampReturnDateAsync(At(13));

        var res = await _sut.GetDaySummaryAsync(Day, _ownerId, UserRole.Owner, scopedToUserId: null);
        Assert.True(res.Succeeded);
        var s = res.Value!;

        Assert.Equal(1000m, s.CashSales);      // only the cash sale
        Assert.Equal(300m, s.CashRefunds);     // only the cash-parent refund
        // 0 + 1000 + 0 − 300 − 0 = 700.
        Assert.Equal(700m, s.ExpectedCash);
    }

    // ---- 5. Cashier scoping ----

    [Fact]
    public async Task CashierScoping_ScopedCountsOnlyThatUser_NullCountsBoth()
    {
        var p = AddProduct("Paracetamol");
        AddBatch(p.ProductId, "B1", qty: 10000m, salePrice: 100m);

        await SaleAtAsync(PaymentMode.Cash, p.ProductId, 10m, _cashierAId, At(9));  // A: 1000
        await SaleAtAsync(PaymentMode.Cash, p.ProductId, 4m, _cashierBId, At(10));  // B: 400

        // Scoped to A: only A's 1000 in cash sales + one own-sales row.
        var scopedA = await _sut.GetDaySummaryAsync(Day, _ownerId, UserRole.Owner, scopedToUserId: _cashierAId);
        Assert.True(scopedA.Succeeded);
        Assert.Equal(1000m, scopedA.Value!.CashSales);
        Assert.Equal(1, scopedA.Value.BillCount);
        Assert.Single(scopedA.Value.OwnSales);

        // Null (whole store): both → 1400 and two rows.
        var whole = await _sut.GetDaySummaryAsync(Day, _ownerId, UserRole.Owner, scopedToUserId: null);
        Assert.True(whole.Succeeded);
        Assert.Equal(1400m, whole.Value!.CashSales);
        Assert.Equal(2, whole.Value.BillCount);
        Assert.Equal(2, whole.Value.OwnSales.Count);
    }

    // ---- 6. Empty day ----

    [Fact]
    public async Task EmptyDay_Expected_Equals_OpeningFloat()
    {
        // No transactions; opening float 0 (no prior close).
        var res = await _sut.GetDaySummaryAsync(Day, _ownerId, UserRole.Owner, scopedToUserId: null);
        Assert.True(res.Succeeded);
        Assert.Equal(0m, res.Value!.OpeningFloat);
        Assert.Equal(0m, res.Value.ExpectedCash);
        Assert.Equal(0, res.Value.BillCount);
        Assert.Empty(res.Value.OwnSales);
    }

    // ---- 7. Close persists snapshot ----

    [Fact]
    public async Task Close_PersistsSnapshot_Components_CreatedBy_ClosedAt_Variance()
    {
        var p = AddProduct("Paracetamol");
        AddBatch(p.ProductId, "B1", qty: 10000m, salePrice: 100m);
        int custId = AddCustomer(balance: 100_000m);

        await SaleAtAsync(PaymentMode.Cash, p.ProductId, 10m, _ownerId, At(10)); // cash 1000
        await _customerLedger.RecordReceiptAsync(new CustomerReceiptInput(custId, 200m, PaymentMode.Cash), _ownerId, UserRole.Owner);
        await StampReceiptDateAsync(At(11));

        DateTime before = DateTime.UtcNow.AddSeconds(-1);
        var close = await _sut.CloseDayAsync(new DayEndCloseInput(Day, 0m, 1150m, Note: "short 50"), _ownerId, UserRole.Owner);
        Assert.True(close.Succeeded, close.Error);

        var stored = await _fixture.NewContext().DayEndCloses.SingleAsync();
        Assert.Equal(Day, stored.BusinessDate);
        Assert.Equal(1000m, stored.CashSales);
        Assert.Equal(200m, stored.CashReceipts);
        Assert.Equal(0m, stored.CashRefunds);
        Assert.Equal(0m, stored.CashSupplierPayments);
        Assert.Equal(1200m, stored.ExpectedCash);   // 0 + 1000 + 200
        Assert.Equal(1150m, stored.CountedCash);
        Assert.Equal(-50m, stored.Variance);         // 1150 − 1200
        Assert.Equal(1150m, stored.ClosingCarryForward); // defaults to counted
        Assert.Equal(_ownerId, stored.CreatedBy);
        Assert.True(stored.ClosedAt >= before);
    }

    // ---- 8. Server recompute ignores UI-supplied expected ----

    [Fact]
    public async Task Close_RecomputesExpected_ServerSide_IgnoringUiFloatOnlyTrusted()
    {
        var p = AddProduct("Paracetamol");
        AddBatch(p.ProductId, "B1", qty: 10000m, salePrice: 100m);
        await SaleAtAsync(PaymentMode.Cash, p.ProductId, 10m, _ownerId, At(10)); // cash 1000

        // The input carries only opening float + counted; there is no way to inject a bogus expected.
        // The stored expected must equal the server recompute (0 float + 1000 cash = 1000), not any
        // client figure. Counted matches so no note needed.
        var close = await _sut.CloseDayAsync(new DayEndCloseInput(Day, 0m, 1000m), _ownerId, UserRole.Owner);
        Assert.True(close.Succeeded, close.Error);

        var stored = await _fixture.NewContext().DayEndCloses.SingleAsync();
        Assert.Equal(1000m, stored.ExpectedCash);
        Assert.Equal(0m, stored.Variance);
    }

    // ---- 9. One-close-per-day ----

    [Fact]
    public async Task Close_SecondTimeSameDay_Fails_NoDuplicateRow()
    {
        var p = AddProduct("Paracetamol");
        AddBatch(p.ProductId, "B1", qty: 10000m, salePrice: 100m);
        await SaleAtAsync(PaymentMode.Cash, p.ProductId, 10m, _ownerId, At(10));

        var first = await _sut.CloseDayAsync(new DayEndCloseInput(Day, 0m, 1000m), _ownerId, UserRole.Owner);
        Assert.True(first.Succeeded, first.Error);

        var second = await _sut.CloseDayAsync(new DayEndCloseInput(Day, 0m, 1000m), _ownerId, UserRole.Owner);
        Assert.False(second.Succeeded);
        Assert.Contains("already closed", second.Error!);
        Assert.Equal(1, await _fixture.NewContext().DayEndCloses.CountAsync());
    }

    // ---- 10. Variance requires note ----

    [Fact]
    public async Task Close_NonZeroVariance_WithoutNote_Fails_ZeroVariance_WithoutNote_Ok()
    {
        var p = AddProduct("Paracetamol");
        AddBatch(p.ProductId, "B1", qty: 10000m, salePrice: 100m);
        await SaleAtAsync(PaymentMode.Cash, p.ProductId, 10m, _ownerId, At(10)); // expected 1000

        // Variance ≠ 0 with empty note → Fail, nothing stored.
        var noNote = await _sut.CloseDayAsync(new DayEndCloseInput(Day, 0m, 900m), _ownerId, UserRole.Owner);
        Assert.False(noNote.Succeeded);
        Assert.Contains("note is required", noNote.Error!);
        Assert.Equal(0, await _fixture.NewContext().DayEndCloses.CountAsync());

        // Variance 0 with empty note → OK.
        var ok = await _sut.CloseDayAsync(new DayEndCloseInput(Day, 0m, 1000m), _ownerId, UserRole.Owner);
        Assert.True(ok.Succeeded, ok.Error);
    }

    // ---- 11. Snapshot immutability against later back-dated cash ----

    [Fact]
    public async Task ClosedDay_Snapshot_IsImmutable_Against_LaterBackdatedCash()
    {
        var p = AddProduct("Paracetamol");
        AddBatch(p.ProductId, "B1", qty: 10000m, salePrice: 100m);
        await SaleAtAsync(PaymentMode.Cash, p.ProductId, 10m, _ownerId, At(10)); // cash 1000

        var close = await _sut.CloseDayAsync(new DayEndCloseInput(Day, 0m, 1000m), _ownerId, UserRole.Owner);
        Assert.True(close.Succeeded, close.Error);

        // Insert ANOTHER cash sale back-dated into the already-closed day.
        await SaleAtAsync(PaymentMode.Cash, p.ProductId, 5m, _ownerId, At(15)); // +500 in that day

        // Re-fetch the summary for the closed day: it returns the STORED snapshot, not a recompute.
        var res = await _sut.GetDaySummaryAsync(Day, _ownerId, UserRole.Owner, scopedToUserId: null);
        Assert.True(res.Succeeded);
        Assert.True(res.Value!.IsClosed);
        Assert.Equal(1000m, res.Value.CashSales);   // unchanged — the extra 500 is NOT reflected
        Assert.Equal(1000m, res.Value.ExpectedCash);
        Assert.Equal(0m, res.Value.Variance);
    }

    // ---- 12. Carry-forward ----

    [Fact]
    public async Task CarryForward_DayN1_OpeningFloat_Equals_DayN_ClosingCarryForward()
    {
        var p = AddProduct("Paracetamol");
        AddBatch(p.ProductId, "B1", qty: 10000m, salePrice: 100m);
        await SaleAtAsync(PaymentMode.Cash, p.ProductId, 10m, _ownerId, At(10)); // day1 cash 1000

        // Close day1 with counted 1000, explicit carry-forward 400 (owner leaves 400 in the till).
        var close1 = await _sut.CloseDayAsync(new DayEndCloseInput(Day, 0m, 1000m, ClosingCarryForward: 400m, Note: null), _ownerId, UserRole.Owner);
        Assert.True(close1.Succeeded, close1.Error);
        Assert.Equal(400m, close1.Value!.ClosingCarryForward);

        // Day2 summary: opening float == day1 carry-forward (400).
        DateTime day2 = Day.AddDays(1);
        var day2Summary = await _sut.GetDaySummaryAsync(day2, _ownerId, UserRole.Owner, scopedToUserId: null);
        Assert.True(day2Summary.Succeeded);
        Assert.Equal(400m, day2Summary.Value!.OpeningFloat);
        Assert.Equal(400m, day2Summary.Value.ExpectedCash); // empty day2 → expected == float
    }

    // ---- 13. RBAC ----

    [Fact]
    public void Rbac_Cashier_Has_DayEnd_But_Not_ViewReports()
    {
        var auth = new AuthService(_fixture.Context);
        Assert.True(auth.HasPermission(UserRole.Cashier, Permission.DayEnd));
        Assert.False(auth.HasPermission(UserRole.Cashier, Permission.ViewReports));
    }

    [Fact]
    public async Task Rbac_RoleWithoutDayEnd_IsRefused_FromAllThreeMethods()
    {
        // (UserRole)999 maps to no permissions.
        var role = (UserRole)999;
        var summary = await _sut.GetDaySummaryAsync(Day, _ownerId, role, scopedToUserId: null);
        var close = await _sut.CloseDayAsync(new DayEndCloseInput(Day, 0m, 0m), _ownerId, role);
        var history = await _sut.GetCloseHistoryAsync(Day.AddDays(-30), Day, _ownerId, role, scopedToUserId: null);

        Assert.False(summary.Succeeded);
        Assert.Contains("permission", summary.Error!);
        Assert.False(close.Succeeded);
        Assert.Contains("permission", close.Error!);
        Assert.False(history.Succeeded);
        Assert.Contains("permission", history.Error!);
    }

    [Fact]
    public async Task Rbac_Cashier_CloseDay_IsRejected()
    {
        var p = AddProduct("Paracetamol");
        AddBatch(p.ProductId, "B1", qty: 10000m, salePrice: 100m);
        await SaleAtAsync(PaymentMode.Cash, p.ProductId, 10m, _cashierAId, At(10));

        var close = await _sut.CloseDayAsync(new DayEndCloseInput(Day, 0m, 1000m), _cashierAId, UserRole.Cashier);
        Assert.False(close.Succeeded);
        Assert.Contains("cashier cannot close", close.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await _fixture.NewContext().DayEndCloses.CountAsync());
    }

    [Fact]
    public async Task Rbac_Cashier_RequestingAnotherUsersSummary_IsCoercedToOwn()
    {
        var p = AddProduct("Paracetamol");
        AddBatch(p.ProductId, "B1", qty: 10000m, salePrice: 100m);
        await SaleAtAsync(PaymentMode.Cash, p.ProductId, 10m, _cashierAId, At(9)); // A: 1000
        await SaleAtAsync(PaymentMode.Cash, p.ProductId, 4m, _cashierBId, At(10)); // B: 400

        // Cashier A asks (maliciously) for B's scope — server coerces to A's own id.
        var res = await _sut.GetDaySummaryAsync(Day, _cashierAId, UserRole.Cashier, scopedToUserId: _cashierBId);
        Assert.True(res.Succeeded);
        Assert.Equal(1000m, res.Value!.CashSales); // A's own, not B's
        Assert.Single(res.Value.OwnSales);
    }

    // ---- 14. ACID rollback on forced mid-close failure ----

    [Fact]
    public async Task Close_WhenPersistFails_RollsBack_NoOrphanRow()
    {
        var p = AddProduct("Paracetamol");
        AddBatch(p.ProductId, "B1", qty: 10000m, salePrice: 100m);
        await SaleAtAsync(PaymentMode.Cash, p.ProductId, 10m, _ownerId, At(10));

        var auth = new AuthService(_fixture.Context);
        using var faulting = new ThrowOnSaveDbContext(_fixture.Options);
        var faultingSut = new DayEndService(faulting, auth);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            faultingSut.CloseDayAsync(new DayEndCloseInput(Day, 0m, 1000m), _ownerId, UserRole.Owner));

        Assert.Equal(0, await _fixture.NewContext().DayEndCloses.CountAsync());
    }

    // ---- Close history ----

    [Fact]
    public async Task CloseHistory_ReturnsClosedDays_MostRecentFirst()
    {
        var p = AddProduct("Paracetamol");
        AddBatch(p.ProductId, "B1", qty: 100000m, salePrice: 100m);

        await SaleAtAsync(PaymentMode.Cash, p.ProductId, 10m, _ownerId, At(10));
        await _sut.CloseDayAsync(new DayEndCloseInput(Day, 0m, 1000m), _ownerId, UserRole.Owner);

        DateTime day2 = Day.AddDays(1);
        await SaleAtAsync(PaymentMode.Cash, p.ProductId, 5m, _ownerId, day2.AddHours(10));
        await _sut.CloseDayAsync(new DayEndCloseInput(day2, 1000m, 1500m), _ownerId, UserRole.Owner);

        var history = await _sut.GetCloseHistoryAsync(Day.AddDays(-5), day2.AddDays(1), _ownerId, UserRole.Owner, scopedToUserId: null);
        Assert.True(history.Succeeded);
        var rows = history.Value!;
        Assert.Equal(2, rows.Count);
        Assert.Equal(day2, rows[0].BusinessDate);  // most-recent first
        Assert.Equal(Day, rows[1].BusinessDate);
        Assert.Equal("Owner", rows[0].ClosedByName);
    }
}
