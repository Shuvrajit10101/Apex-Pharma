using System;
using System.Linq;
using System.Threading.Tasks;
using ApexPharma.Application.Services;
using ApexPharma.Application.Services.Ledger;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ApexPharma.Tests;

/// <summary>
/// SupplierLedgerService tests (plan.md §3, §6.1, §11) — the supplier account ledger. The payable
/// is DERIVED (no stored balance): OpeningBalance + ΣPurchase.Total − ΣPurchaseReturn − ΣPayment.
/// Cover: running-balance correctness (opening balance → purchase → purchase-return → payment) with
/// exact ordered balances; opening-balance carry-forward (including a supplier OpeningBalance) into
/// a date window; a payment writes a SupplierPayment row and reduces the derived payable; over-payment
/// blocked (no row written); audit fields persisted; and RBAC (DoPurchases to record, ViewReports to
/// view).
/// </summary>
public class SupplierLedgerServiceTests : IDisposable
{
    private readonly SqliteInMemoryContext _fixture = new();
    private readonly PurchaseService _purchases;
    private readonly SupplierLedgerService _sut;
    private int _userId;
    private int _catId;
    private int _manId;

    public SupplierLedgerServiceTests()
    {
        var auth = new AuthService(_fixture.Context);
        var gst = new GstService();
        _purchases = new PurchaseService(_fixture.Context, auth, gst);
        _sut = new SupplierLedgerService(_fixture.Context, auth, TestTz.IstProvider());
        Seed();
    }

    public void Dispose() => _fixture.Dispose();

    private void Seed()
    {
        var db = _fixture.Context;
        var role = new Role { Name = "Owner" };
        db.Roles.Add(role);
        db.SaveChanges();
        var user = new User { Username = "owner", PasswordHash = "x", FullName = "Owner", RoleId = role.RoleId };
        var cat = new Category { Name = "Medication" };
        var man = new Manufacturer { Name = "Cipla" };
        db.Users.Add(user);
        db.Categories.Add(cat);
        db.Manufacturers.Add(man);
        db.SaveChanges();
        _userId = user.UserId;
        _catId = cat.CategoryId;
        _manId = man.ManufacturerId;
    }

    private int AddSupplier(string name = "MediDist", decimal openingBalance = 0m)
    {
        var db = _fixture.Context;
        var s = new Supplier { Name = name, IsActive = true, OpeningBalance = openingBalance };
        db.Suppliers.Add(s);
        db.SaveChanges();
        return s.SupplierId;
    }

    private Product AddProduct(string name, decimal gstRate = 0m)
    {
        var db = _fixture.Context;
        var p = new Product { Name = name, CategoryId = _catId, ManufacturerId = _manId, GstRate = gstRate, IsActive = true, ReorderLevel = 0 };
        db.Products.Add(p);
        db.SaveChanges();
        return p;
    }

    /// <summary>Records a purchase of one line (GST 0 so Total == qty*price for clean assertions).</summary>
    private async Task<Purchase> RecordPurchaseAsync(int supplierId, int productId, string batchNo, decimal qty, decimal price)
    {
        var input = new PurchaseInput
        {
            SupplierId = supplierId,
            SupplierInvoiceNo = $"SINV-{batchNo}",
            InvoiceDate = DateTime.UtcNow,
            Lines =
            {
                new PurchaseLineInput
                {
                    ProductId = productId, BatchNo = batchNo, ExpiryDate = DateTime.UtcNow.Date.AddYears(1),
                    Qty = qty, PurchasePrice = price, Mrp = price, GstRate = 0m,
                },
            },
        };
        var result = await _purchases.RecordPurchaseAsync(input, _userId, UserRole.Owner);
        Assert.True(result.Succeeded, result.Error);
        return result.Value!;
    }

    // ---- Running balance across purchase → purchase-return → payment ----

    [Fact]
    public async Task Statement_RunningBalance_AcrossPurchaseReturnAndPayment()
    {
        int supplierId = AddSupplier(openingBalance: 0m);
        var p = AddProduct("Paracetamol");

        // Purchase 100 @ 10 (GST 0) => Total 1000 debit. Payable -> 1000.
        var purchase = await RecordPurchaseAsync(supplierId, p.ProductId, "B1", 100m, 10m);
        Assert.Equal(1000m, purchase.Total);

        // Purchase-return of 20 units @ 10 => 200 credit. Payable -> 800.
        var batch = await _fixture.Context.Batches.FirstAsync(b => b.ProductId == p.ProductId && b.BatchNo == "B1");
        var ret = await _purchases.ProcessPurchaseReturnAsync(purchase.PurchaseId, batch.BatchId, 20m, "damaged", _userId, UserRole.Owner);
        Assert.True(ret.Succeeded, ret.Error);
        Assert.Equal(200m, ret.Value!.Amount);

        // Payment of 300 => credit. Payable -> 500.
        var pay = await _sut.RecordPaymentAsync(new SupplierPaymentInput(supplierId, 300m, PaymentMode.Cash), _userId, UserRole.Owner);
        Assert.True(pay.Succeeded, pay.Error);

        var stmt = await _sut.GetStatementAsync(supplierId, DateTime.Today.AddYears(-1), DateTime.Today, UserRole.Owner);
        Assert.True(stmt.Succeeded);
        var s = stmt.Value!;
        Assert.Equal(0m, s.OpeningBalance);
        Assert.Equal(4, s.Rows.Count);

        Assert.Equal("Opening balance", s.Rows[0].DocType);
        Assert.Equal(0m, s.Rows[0].RunningBalance);
        Assert.Equal("Purchase", s.Rows[1].DocType);
        Assert.Equal(1000m, s.Rows[1].Debit);
        Assert.Equal(1000m, s.Rows[1].RunningBalance);
        Assert.Equal("Purchase return", s.Rows[2].DocType);
        Assert.Equal(200m, s.Rows[2].Credit);
        Assert.Equal(800m, s.Rows[2].RunningBalance);
        Assert.Equal("Payment", s.Rows[3].DocType);
        Assert.Equal(300m, s.Rows[3].Credit);
        Assert.Equal(500m, s.Rows[3].RunningBalance);

        Assert.Equal(500m, s.ClosingBalance);
    }

    // ---- Supplier OpeningBalance carry-forward into a window ----

    [Fact]
    public async Task Statement_CarriesForwardOpeningBalanceAndPreWindowTxns()
    {
        int supplierId = AddSupplier(openingBalance: 250m);
        var p = AddProduct("Paracetamol");

        // A purchase dated before the window.
        var purchase = await RecordPurchaseAsync(supplierId, p.ProductId, "B1", 100m, 10m); // +1000
        DateTime old = DateTime.UtcNow.AddDays(-30);
        var pr = await _fixture.Context.Purchases.SingleAsync();
        pr.InvoiceDate = old;
        await _fixture.Context.SaveChangesAsync();

        // A payment INSIDE the window (today).
        await _sut.RecordPaymentAsync(new SupplierPaymentInput(supplierId, 400m, PaymentMode.Upi), _userId, UserRole.Owner); // -400

        // Window = today only. Opening = 250 (opening bal) + 1000 (pre-window purchase) = 1250.
        var stmt = await _sut.GetStatementAsync(supplierId, DateTime.Today, DateTime.Today, UserRole.Owner);
        Assert.True(stmt.Succeeded);
        var s = stmt.Value!;
        Assert.Equal(1250m, s.OpeningBalance);
        Assert.Equal(1250m, s.Rows[0].RunningBalance);
        Assert.Equal(2, s.Rows.Count);                          // opening + one in-window payment
        Assert.Equal("Payment", s.Rows[1].DocType);
        Assert.Equal(400m, s.Rows[1].Credit);
        Assert.Equal(850m, s.Rows[1].RunningBalance);
        Assert.Equal(850m, s.ClosingBalance);
    }

    // ---- Explicit-instant IST boundary regression (host-TZ-independent) ----

    [Fact]
    public async Task Statement_PaymentAtIstMidnightPlus5Min_LandsInIstDayWindow()
    {
        // Pin the fix regardless of the host machine's timezone: a payment stamped at the UTC instant
        // equal to IST D 00:05 (= D-1 18:35Z) must fall INSIDE the [D] statement window; one at IST
        // (D-1) 23:55 (= D-1 18:25Z) must fall in the PRIOR day (carried into opening).
        int supplierId = AddSupplier(openingBalance: 0m);
        var p = AddProduct("Paracetamol");
        await RecordPurchaseAsync(supplierId, p.ProductId, "B1", 100m, 10m); // payable 1000
        // Backdate the purchase far before the window so it folds into opening cleanly.
        var pr = await _fixture.Context.Purchases.SingleAsync();
        pr.InvoiceDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        await _fixture.Context.SaveChangesAsync();

        DateTime d = new DateTime(2026, 6, 15);                 // local calendar day D
        DateTime istMidnightPlus5 = new DateTime(2026, 6, 14, 18, 35, 0, DateTimeKind.Utc); // IST D 00:05
        DateTime istPrevDay2355 = new DateTime(2026, 6, 14, 18, 25, 0, DateTimeKind.Utc);    // IST D-1 23:55

        await _sut.RecordPaymentAsync(new SupplierPaymentInput(supplierId, 40m, PaymentMode.Cash), _userId, UserRole.Owner);
        await _sut.RecordPaymentAsync(new SupplierPaymentInput(supplierId, 30m, PaymentMode.Cash), _userId, UserRole.Owner);

        var live = _fixture.Context;
        var pays = await live.SupplierPayments.OrderBy(x => x.SupplierPaymentId).ToListAsync();
        pays[0].PaymentDate = istPrevDay2355;   // prior IST day
        pays[1].PaymentDate = istMidnightPlus5; // IST day D, 00:05
        await live.SaveChangesAsync();

        var stmt = await _sut.GetStatementAsync(supplierId, d, d, UserRole.Owner);
        Assert.True(stmt.Succeeded, stmt.Error);
        var s = stmt.Value!;

        // Opening = 1000 (pre-window purchase) − 40 (the 23:55 payment folds in) = 960.
        Assert.Equal(960m, s.OpeningBalance);
        // Only the IST-D-00:05 payment (30) is in-window.
        Assert.Equal(2, s.Rows.Count);                 // opening + one in-window payment
        Assert.Equal("Payment", s.Rows[1].DocType);
        Assert.Equal(30m, s.Rows[1].Credit);
        Assert.Equal(930m, s.Rows[1].RunningBalance);  // 960 − 30
        Assert.DoesNotContain(s.Rows, r => r.Credit == 40m); // the 23:55 payment is NOT in-window
    }

    // ---- Payment writes a row and reduces the derived payable ----

    [Fact]
    public async Task RecordPayment_WritesRow_AndReducesDerivedPayable()
    {
        int supplierId = AddSupplier(openingBalance: 0m);
        var p = AddProduct("Paracetamol");
        await RecordPurchaseAsync(supplierId, p.ProductId, "B1", 100m, 10m); // payable 1000

        var pay = await _sut.RecordPaymentAsync(new SupplierPaymentInput(supplierId, 400m, PaymentMode.Cash), _userId, UserRole.Owner);

        Assert.True(pay.Succeeded);
        Assert.Equal(1, await _fixture.NewContext().SupplierPayments.CountAsync());

        // Derived payable now 600; a second payment of 600 must succeed, 601 must fail.
        var exact = await _sut.RecordPaymentAsync(new SupplierPaymentInput(supplierId, 600m, PaymentMode.Cash), _userId, UserRole.Owner);
        Assert.True(exact.Succeeded);

        var over = await _sut.RecordPaymentAsync(new SupplierPaymentInput(supplierId, 0.01m, PaymentMode.Cash), _userId, UserRole.Owner);
        Assert.False(over.Succeeded); // payable now 0
    }

    // ---- Over-payment blocked ----

    [Fact]
    public async Task RecordPayment_OverDerivedPayable_IsBlocked_NoRow()
    {
        int supplierId = AddSupplier(openingBalance: 0m);
        var p = AddProduct("Paracetamol");
        await RecordPurchaseAsync(supplierId, p.ProductId, "B1", 10m, 10m); // payable 100

        var result = await _sut.RecordPaymentAsync(new SupplierPaymentInput(supplierId, 150m, PaymentMode.Cash), _userId, UserRole.Owner);

        Assert.False(result.Succeeded);
        Assert.Contains("exceeds", result.Error!);
        Assert.Equal(0, await _fixture.NewContext().SupplierPayments.CountAsync());
    }

    [Fact]
    public async Task RecordPayment_ExactlyEqualToPayable_Succeeds_ThenAnyMoreIsBlocked()
    {
        // Boundary (mirrors the customer exact-equal test): a payment of EXACTLY the derived
        // payable succeeds and drives it to zero; a further 0.01 then fails with the over-payable
        // error and writes no additional row.
        int supplierId = AddSupplier(openingBalance: 0m);
        var p = AddProduct("Paracetamol");
        await RecordPurchaseAsync(supplierId, p.ProductId, "B1", 10m, 10m); // payable 100

        var exact = await _sut.RecordPaymentAsync(new SupplierPaymentInput(supplierId, 100m, PaymentMode.Cash), _userId, UserRole.Owner);
        Assert.True(exact.Succeeded);
        Assert.Equal(1, await _fixture.NewContext().SupplierPayments.CountAsync());

        var over = await _sut.RecordPaymentAsync(new SupplierPaymentInput(supplierId, 0.01m, PaymentMode.Cash), _userId, UserRole.Owner);
        Assert.False(over.Succeeded);
        Assert.Contains("exceeds", over.Error!);
        Assert.Equal(1, await _fixture.NewContext().SupplierPayments.CountAsync()); // still just the one
    }

    [Fact]
    public async Task RecordPayment_WhenPersistFails_RollsBack_PayableUnchanged_NoRow()
    {
        // Genuine post-mutation rollback: the service adds the payment row in the change tracker,
        // then persist throws — the ACID transaction must roll back so a fresh context sees no
        // SupplierPayment row and the derived payable recomputes to its original value.
        int supplierId = AddSupplier(openingBalance: 0m);
        var p = AddProduct("Paracetamol");
        await RecordPurchaseAsync(supplierId, p.ProductId, "B1", 10m, 10m); // payable 100

        var auth = new AuthService(_fixture.Context);
        using var faulting = new ThrowOnSaveDbContext(_fixture.Options);
        var faultingSut = new SupplierLedgerService(faulting, auth, TestTz.IstProvider());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            faultingSut.RecordPaymentAsync(new SupplierPaymentInput(supplierId, 40m, PaymentMode.Cash), _userId, UserRole.Owner));

        Assert.Equal(0, await _fixture.NewContext().SupplierPayments.CountAsync()); // no row committed

        // Derived payable is unchanged (still 100): a fresh statement's closing balance proves it.
        var stmt = await _sut.GetStatementAsync(supplierId, DateTime.Today.AddYears(-1), DateTime.Today, UserRole.Owner);
        Assert.True(stmt.Succeeded);
        Assert.Equal(100m, stmt.Value!.ClosingBalance);
    }

    [Fact]
    public async Task RecordPayment_NonPositiveAmount_IsBlocked()
    {
        int supplierId = AddSupplier(openingBalance: 0m);
        var p = AddProduct("Paracetamol");
        await RecordPurchaseAsync(supplierId, p.ProductId, "B1", 10m, 10m);

        var zero = await _sut.RecordPaymentAsync(new SupplierPaymentInput(supplierId, 0m, PaymentMode.Cash), _userId, UserRole.Owner);
        var negative = await _sut.RecordPaymentAsync(new SupplierPaymentInput(supplierId, -5m, PaymentMode.Cash), _userId, UserRole.Owner);

        Assert.False(zero.Succeeded);
        Assert.False(negative.Succeeded);
        Assert.Equal(0, await _fixture.NewContext().SupplierPayments.CountAsync());
    }

    [Fact]
    public async Task RecordPayment_MissingSupplier_IsBlocked()
    {
        var result = await _sut.RecordPaymentAsync(new SupplierPaymentInput(9999, 50m, PaymentMode.Cash), _userId, UserRole.Owner);

        Assert.False(result.Succeeded);
        Assert.Contains("valid supplier", result.Error!);
        Assert.Equal(0, await _fixture.NewContext().SupplierPayments.CountAsync());
    }

    // ---- Audit fields ----

    [Fact]
    public async Task RecordPayment_PersistsAuditFields()
    {
        int supplierId = AddSupplier(openingBalance: 500m);
        DateTime before = DateTime.UtcNow.AddSeconds(-1);

        var result = await _sut.RecordPaymentAsync(
            new SupplierPaymentInput(supplierId, 200m, PaymentMode.Card, Reference: "CHQ-77", Note: " partial "), _userId, UserRole.Owner);

        Assert.True(result.Succeeded);
        var payment = await _fixture.NewContext().SupplierPayments.SingleAsync();
        Assert.Equal(_userId, payment.CreatedBy);
        Assert.Equal(200m, payment.Amount);
        Assert.Equal(PaymentMode.Card, payment.PaymentMode);
        Assert.Equal("CHQ-77", payment.Reference);
        Assert.Equal("partial", payment.Note);               // trimmed
        Assert.True(payment.PaymentDate >= before);
    }

    // ---- RBAC ----

    [Fact]
    public async Task RecordPayment_WithoutDoPurchases_IsRefused()
    {
        int supplierId = AddSupplier(openingBalance: 500m);

        // Cashier lacks DoPurchases.
        var result = await _sut.RecordPaymentAsync(new SupplierPaymentInput(supplierId, 100m, PaymentMode.Cash), _userId, UserRole.Cashier);

        Assert.False(result.Succeeded);
        Assert.Contains("permission", result.Error!);
        Assert.Equal(0, await _fixture.NewContext().SupplierPayments.CountAsync());
    }

    [Fact]
    public async Task RecordPayment_AsPharmacist_Succeeds()
    {
        int supplierId = AddSupplier(openingBalance: 500m);

        var result = await _sut.RecordPaymentAsync(new SupplierPaymentInput(supplierId, 100m, PaymentMode.Cash), _userId, UserRole.Pharmacist);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task GetStatement_WithoutViewReports_IsRefused()
    {
        int supplierId = AddSupplier(openingBalance: 500m);

        // Cashier lacks ViewReports.
        var result = await _sut.GetStatementAsync(supplierId, DateTime.Today.AddDays(-30), DateTime.Today, UserRole.Cashier);

        Assert.False(result.Succeeded);
        Assert.Contains("permission", result.Error!);
    }
}
