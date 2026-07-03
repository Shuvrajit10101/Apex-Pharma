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
/// CustomerLedgerService tests (plan.md §3, §6.1, §11) — the customer khata ledger. Cover:
/// running-balance correctness across a mixed sequence (opening → credit sale → sales-return →
/// receipt) with exact ordered balances and closing == Customer.Balance for an all-time window;
/// opening-balance carry-forward into a date window (pre-window txns fold into the opening row,
/// only in-range rows appear); a receipt reduces Customer.Balance transactionally; over-payment
/// blocked (no mutation); cash sales excluded from the khata statement; audit fields persisted;
/// and RBAC (DoBilling to record, ViewReports to view).
/// </summary>
public class CustomerLedgerServiceTests : IDisposable
{
    private readonly SqliteInMemoryContext _fixture = new();
    private readonly BillingService _billing;
    private readonly SaleReturnService _returns;
    private readonly CustomerLedgerService _sut;
    private int _userId;
    private int _supplierId;
    private int _catId;
    private int _manId;

    public CustomerLedgerServiceTests()
    {
        var auth = new AuthService(_fixture.Context);
        var gst = new GstService();
        _billing = new BillingService(_fixture.Context, auth, gst);
        _returns = new SaleReturnService(_fixture.Context, auth);
        _sut = new CustomerLedgerService(_fixture.Context, auth, TestTz.IstProvider());
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
        db.Users.Add(user);
        var cat = new Category { Name = "Medication" };
        var man = new Manufacturer { Name = "Cipla" };
        var supplier = new Supplier { Name = "MediDist", IsActive = true };
        db.Categories.Add(cat);
        db.Manufacturers.Add(man);
        db.Suppliers.Add(supplier);
        db.SaveChanges();
        _userId = user.UserId;
        _supplierId = supplier.SupplierId;
        _catId = cat.CategoryId;
        _manId = man.ManufacturerId;
    }

    private Product AddProduct(string name, decimal gstRate = 12m)
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

    private async Task<(string BillNo, int SaleItemId, decimal Total)> CreditSaleAsync(int customerId, int productId, decimal qty)
    {
        var input = new SaleInput { PaymentMode = PaymentMode.Credit, CustomerId = customerId, Lines = { new SaleLineInput { ProductId = productId, Qty = qty } } };
        var sale = await _billing.CreateSaleAsync(input, UserRole.Owner, _userId);
        Assert.True(sale.Succeeded);
        int saleItemId = (await _fixture.NewContext().SaleItems.OrderByDescending(i => i.SaleItemId).FirstAsync()).SaleItemId;
        return (sale.Value!.BillNo, saleItemId, sale.Value.Total);
    }

    // ---- Running balance across a mixed sequence; closing == Customer.Balance ----

    [Fact]
    public async Task Statement_RunningBalance_AcrossMixedSequence_ClosesToCustomerBalance()
    {
        var p = AddProduct("Paracetamol", gstRate: 12m);
        AddBatch(p.ProductId, "B1", qty: 1000m, salePrice: 20m);
        int customerId = AddCustomer(balance: 0m);

        // Credit sale of 10 @ 20 => 200 taxable + 24 GST = 224 debit. Balance -> 224.
        var sale = await CreditSaleAsync(customerId, p.ProductId, 10m);
        Assert.Equal(224m, (await _fixture.NewContext().Customers.SingleAsync()).Balance);

        // Sales-return of 5 => credit 112. Balance -> 112.
        var ret = await _returns.ProcessSaleReturnAsync(
            new SaleReturnInput { BillNo = sale.BillNo, Lines = { new SaleReturnLineInput { SaleItemId = sale.SaleItemId, Qty = 5m } } },
            _userId, UserRole.Owner);
        Assert.True(ret.Succeeded);
        Assert.Equal(112m, (await _fixture.NewContext().Customers.SingleAsync()).Balance);

        // Receipt of 100 => credit. Balance -> 12.
        var rcpt = await _sut.RecordReceiptAsync(new CustomerReceiptInput(customerId, 100m, PaymentMode.Cash), _userId, UserRole.Owner);
        Assert.True(rcpt.Succeeded);
        Assert.Equal(12m, (await _fixture.NewContext().Customers.SingleAsync()).Balance);

        // All-time statement: opening 0, three txns, closing 12 == Customer.Balance.
        var stmt = await _sut.GetStatementAsync(customerId, DateTime.Today.AddYears(-1), DateTime.Today, UserRole.Owner);
        Assert.True(stmt.Succeeded);
        var s = stmt.Value!;
        Assert.Equal(0m, s.OpeningBalance);

        // Rows: opening + credit sale (debit 224 -> 224) + sales return (credit 112 -> 112) + receipt (credit 100 -> 12).
        Assert.Equal(4, s.Rows.Count);
        Assert.Equal("Opening balance", s.Rows[0].DocType);
        Assert.Equal(0m, s.Rows[0].RunningBalance);
        Assert.Equal("Credit sale", s.Rows[1].DocType);
        Assert.Equal(224m, s.Rows[1].Debit);
        Assert.Equal(224m, s.Rows[1].RunningBalance);
        Assert.Equal("Sales return", s.Rows[2].DocType);
        Assert.Equal(112m, s.Rows[2].Credit);
        Assert.Equal(112m, s.Rows[2].RunningBalance);
        Assert.Equal("Receipt", s.Rows[3].DocType);
        Assert.Equal(100m, s.Rows[3].Credit);
        Assert.Equal(12m, s.Rows[3].RunningBalance);

        Assert.Equal(12m, s.ClosingBalance);
        Assert.Equal(12m, (await _fixture.NewContext().Customers.SingleAsync()).Balance);
    }

    // ---- Opening-balance carry-forward into a window ----

    [Fact]
    public async Task Statement_CarriesForwardPreWindowTxns_IntoOpeningRow()
    {
        var p = AddProduct("Paracetamol", gstRate: 12m);
        AddBatch(p.ProductId, "B1", qty: 1000m, salePrice: 20m);
        int customerId = AddCustomer(balance: 0m);

        // A credit sale then a receipt, both dated well before the window.
        var sale = await CreditSaleAsync(customerId, p.ProductId, 10m); // +224
        await _sut.RecordReceiptAsync(new CustomerReceiptInput(customerId, 24m, PaymentMode.Cash), _userId, UserRole.Owner); // -24 -> 200

        // Backdate BOTH transactions to 30 days ago so they fall strictly before the window.
        DateTime old = DateTime.UtcNow.AddDays(-30);
        var live = _fixture.Context;
        foreach (var srow in await live.Sales.ToListAsync()) { srow.BillDate = old; }
        foreach (var rrow in await live.CustomerReceipts.ToListAsync()) { rrow.ReceiptDate = old; }
        await live.SaveChangesAsync();

        // A receipt INSIDE the window (today).
        await _sut.RecordReceiptAsync(new CustomerReceiptInput(customerId, 50m, PaymentMode.Upi), _userId, UserRole.Owner); // -50 -> 150

        // Window = today only. Pre-window net = 224 - 24 = 200 folds into the opening row; only the
        // today receipt appears in-range.
        var stmt = await _sut.GetStatementAsync(customerId, DateTime.Today, DateTime.Today, UserRole.Owner);
        Assert.True(stmt.Succeeded);
        var s = stmt.Value!;
        Assert.Equal(200m, s.OpeningBalance);
        Assert.Equal(200m, s.Rows[0].RunningBalance);           // opening row
        Assert.Equal(2, s.Rows.Count);                          // opening + one in-window receipt
        Assert.Equal("Receipt", s.Rows[1].DocType);
        Assert.Equal(50m, s.Rows[1].Credit);
        Assert.Equal(150m, s.Rows[1].RunningBalance);
        Assert.Equal(150m, s.ClosingBalance);
    }

    // ---- Explicit-instant IST boundary regression (host-TZ-independent) ----

    [Fact]
    public async Task Statement_ReceiptAtIstMidnightPlus5Min_LandsInIstDayWindow()
    {
        // Pin the fix regardless of the host machine's timezone: a receipt stamped at the UTC instant
        // equal to IST D 00:05 (= D-1 18:35Z) must fall INSIDE the [D] statement window, and a receipt
        // stamped at IST (D-1) 23:55 (= D-1 18:25Z) must fall in the PRIOR day (carried into opening).
        var p = AddProduct("Paracetamol", gstRate: 12m);
        AddBatch(p.ProductId, "B1", qty: 1000m, salePrice: 20m);
        int customerId = AddCustomer(balance: 0m);

        DateTime d = new DateTime(2026, 6, 15);                 // local calendar day D
        DateTime istMidnightPlus5 = new DateTime(2026, 6, 14, 18, 35, 0, DateTimeKind.Utc); // IST D 00:05
        DateTime istPrevDay2355 = new DateTime(2026, 6, 14, 18, 25, 0, DateTimeKind.Utc);    // IST D-1 23:55

        // Two credit sales so both receipts have balance to reduce.
        await CreditSaleAsync(customerId, p.ProductId, 10m); // +224
        await CreditSaleAsync(customerId, p.ProductId, 10m); // +224 -> 448

        // Record two receipts, then stamp them at the two boundary instants.
        await _sut.RecordReceiptAsync(new CustomerReceiptInput(customerId, 40m, PaymentMode.Cash), _userId, UserRole.Owner);
        await _sut.RecordReceiptAsync(new CustomerReceiptInput(customerId, 30m, PaymentMode.Cash), _userId, UserRole.Owner);

        var live = _fixture.Context;
        var rcpts = await live.CustomerReceipts.OrderBy(r => r.CustomerReceiptId).ToListAsync();
        rcpts[0].ReceiptDate = istPrevDay2355;   // prior IST day
        rcpts[1].ReceiptDate = istMidnightPlus5; // IST day D, 00:05
        await live.SaveChangesAsync();

        var stmt = await _sut.GetStatementAsync(customerId, d, d, UserRole.Owner);
        Assert.True(stmt.Succeeded, stmt.Error);
        var s = stmt.Value!;

        // The IST-D-00:05 receipt (30) is the ONLY in-window row; the 23:55 receipt (40) folds into opening.
        Assert.Equal(2, s.Rows.Count);                 // opening + one in-window receipt
        Assert.Equal("Receipt", s.Rows[1].DocType);
        Assert.Equal(30m, s.Rows[1].Credit);
        // Opening = 448 (both sales, dated now, are before D=2026-06-15... but sales are stamped UtcNow).
        // Assert the boundary receipt bucketing directly: exactly one in-window row, and it is the 30.
        Assert.DoesNotContain(s.Rows, r => r.Credit == 40m); // the 23:55 receipt is NOT in-window
    }

    // ---- Receipt reduces Customer.Balance transactionally ----

    [Fact]
    public async Task RecordReceipt_ReducesCustomerBalance_Exactly()
    {
        int customerId = AddCustomer(balance: 500m);

        var result = await _sut.RecordReceiptAsync(new CustomerReceiptInput(customerId, 200m, PaymentMode.Cash), _userId, UserRole.Owner);

        Assert.True(result.Succeeded);
        Assert.Equal(300m, (await _fixture.NewContext().Customers.SingleAsync()).Balance);
        Assert.Equal(1, await _fixture.NewContext().CustomerReceipts.CountAsync());
    }

    // ---- Over-payment blocked ----

    [Fact]
    public async Task RecordReceipt_OverKhata_IsBlocked_NoMutation()
    {
        int customerId = AddCustomer(balance: 100m);

        var result = await _sut.RecordReceiptAsync(new CustomerReceiptInput(customerId, 150m, PaymentMode.Cash), _userId, UserRole.Owner);

        Assert.False(result.Succeeded);
        Assert.Contains("exceeds", result.Error!);
        Assert.Equal(100m, (await _fixture.NewContext().Customers.SingleAsync()).Balance); // unchanged
        Assert.Equal(0, await _fixture.NewContext().CustomerReceipts.CountAsync());        // nothing persisted
    }

    [Fact]
    public async Task RecordReceipt_ExactlyEqualToBalance_Succeeds_ThenAnyMoreIsBlocked()
    {
        // Boundary: a receipt of EXACTLY the outstanding balance is allowed and drives it to zero;
        // a further 0.01 then fails with the over-khata error and leaves the balance at zero.
        int customerId = AddCustomer(balance: 100m);

        var exact = await _sut.RecordReceiptAsync(new CustomerReceiptInput(customerId, 100m, PaymentMode.Cash), _userId, UserRole.Owner);
        Assert.True(exact.Succeeded);
        Assert.Equal(0m, (await _fixture.NewContext().Customers.SingleAsync()).Balance);
        Assert.Equal(1, await _fixture.NewContext().CustomerReceipts.CountAsync());

        var over = await _sut.RecordReceiptAsync(new CustomerReceiptInput(customerId, 0.01m, PaymentMode.Cash), _userId, UserRole.Owner);
        Assert.False(over.Succeeded);
        Assert.Contains("exceeds", over.Error!);
        Assert.Equal(0m, (await _fixture.NewContext().Customers.SingleAsync()).Balance); // still zero
        Assert.Equal(1, await _fixture.NewContext().CustomerReceipts.CountAsync());       // still just the one
    }

    [Fact]
    public async Task RecordReceipt_WhenPersistFails_RollsBack_NoBalanceChange_NoRow()
    {
        // Genuine post-mutation rollback: the service reduces the balance and adds the receipt in
        // the change tracker, then persist throws — the ACID transaction must roll back so a fresh
        // context sees the ORIGINAL balance and no CustomerReceipt row.
        int customerId = AddCustomer(balance: 500m);

        var auth = new AuthService(_fixture.Context);
        using var faulting = new ThrowOnSaveDbContext(_fixture.Options);
        var faultingSut = new CustomerLedgerService(faulting, auth, TestTz.IstProvider());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            faultingSut.RecordReceiptAsync(new CustomerReceiptInput(customerId, 200m, PaymentMode.Cash), _userId, UserRole.Owner));

        var fresh = _fixture.NewContext();
        Assert.Equal(500m, (await fresh.Customers.SingleAsync(c => c.CustomerId == customerId)).Balance); // unchanged
        Assert.Equal(0, await fresh.CustomerReceipts.CountAsync());                                        // no row
    }

    [Fact]
    public async Task Statement_WindowEdges_AreInclusive_Ist()
    {
        // Timezone-robust boundary under the IST window semantics: the operator picks local dates
        // [2026-06-01, 2026-06-30], which map to the UTC window [2026-05-31 18:30Z, 2026-06-30 18:30Z).
        // Seed receipts at the FIRST instant of that window (IST 06-01 00:00 == 05-31 18:30Z) and at
        // the LAST in-window instant (IST 06-30 23:59 == 06-30 18:29Z); both must appear as in-window
        // rows (not folded into opening, not dropped), independent of the machine's local timezone.
        int customerId = AddCustomer(balance: 1000m);

        DateTime fromLocal = new DateTime(2026, 6, 1);
        DateTime toLocal = new DateTime(2026, 6, 30);
        DateTime fromEdgeUtc = new DateTime(2026, 5, 31, 18, 30, 0, DateTimeKind.Utc); // start of window
        DateTime toEdgeUtc = new DateTime(2026, 6, 30, 18, 29, 0, DateTimeKind.Utc);   // last in-window minute

        await _sut.RecordReceiptAsync(
            new CustomerReceiptInput(customerId, 100m, PaymentMode.Cash, ReceiptDate: fromEdgeUtc), _userId, UserRole.Owner);
        await _sut.RecordReceiptAsync(
            new CustomerReceiptInput(customerId, 150m, PaymentMode.Cash, ReceiptDate: toEdgeUtc), _userId, UserRole.Owner);

        var stmt = await _sut.GetStatementAsync(customerId, fromLocal, toLocal, UserRole.Owner);
        Assert.True(stmt.Succeeded);
        var s = stmt.Value!;

        // No transactions strictly before the window → opening 0. Both edge receipts fall INSIDE the
        // half-open window and appear as rows — which is what this timezone-robust test proves.
        Assert.Equal(0m, s.OpeningBalance);
        Assert.Equal(3, s.Rows.Count);
        Assert.Equal("Opening balance", s.Rows[0].DocType);
        Assert.Equal("Receipt", s.Rows[1].DocType);
        Assert.Equal(100m, s.Rows[1].Credit);   // from-edge receipt
        Assert.Equal("Receipt", s.Rows[2].DocType);
        Assert.Equal(150m, s.Rows[2].Credit);   // to-edge receipt
        Assert.Equal(-250m, s.ClosingBalance);  // 0 − 100 − 150 (both credits applied)
    }

    [Fact]
    public async Task RecordReceipt_NonPositiveAmount_IsBlocked()
    {
        int customerId = AddCustomer(balance: 100m);

        var zero = await _sut.RecordReceiptAsync(new CustomerReceiptInput(customerId, 0m, PaymentMode.Cash), _userId, UserRole.Owner);
        var negative = await _sut.RecordReceiptAsync(new CustomerReceiptInput(customerId, -5m, PaymentMode.Cash), _userId, UserRole.Owner);

        Assert.False(zero.Succeeded);
        Assert.False(negative.Succeeded);
        Assert.Equal(0, await _fixture.NewContext().CustomerReceipts.CountAsync());
    }

    // ---- Cash sale excluded ----

    [Fact]
    public async Task Statement_ExcludesCashSales()
    {
        var p = AddProduct("Paracetamol", gstRate: 12m);
        AddBatch(p.ProductId, "B1", qty: 1000m, salePrice: 20m);
        int customerId = AddCustomer(balance: 0m);

        // A CASH sale attached to the customer — must NOT appear as a khata debit.
        var cash = new SaleInput { PaymentMode = PaymentMode.Cash, CustomerId = customerId, Lines = { new SaleLineInput { ProductId = p.ProductId, Qty = 10m } } };
        var sale = await _billing.CreateSaleAsync(cash, UserRole.Owner, _userId);
        Assert.True(sale.Succeeded);

        var stmt = await _sut.GetStatementAsync(customerId, DateTime.Today.AddYears(-1), DateTime.Today, UserRole.Owner);
        Assert.True(stmt.Succeeded);
        var s = stmt.Value!;
        Assert.Single(s.Rows);                              // only the opening row
        Assert.Equal("Opening balance", s.Rows[0].DocType);
        Assert.Equal(0m, s.ClosingBalance);
    }

    // ---- Audit fields ----

    [Fact]
    public async Task RecordReceipt_PersistsAuditFields()
    {
        int customerId = AddCustomer(balance: 500m);
        DateTime before = DateTime.UtcNow.AddSeconds(-1);

        var result = await _sut.RecordReceiptAsync(
            new CustomerReceiptInput(customerId, 200m, PaymentMode.Upi, Reference: "UPI-123", Note: " counter "), _userId, UserRole.Owner);

        Assert.True(result.Succeeded);
        var receipt = await _fixture.NewContext().CustomerReceipts.SingleAsync();
        Assert.Equal(_userId, receipt.CreatedBy);
        Assert.Equal(200m, receipt.Amount);
        Assert.Equal(PaymentMode.Upi, receipt.PaymentMode);
        Assert.Equal("UPI-123", receipt.Reference);
        Assert.Equal("counter", receipt.Note);               // trimmed
        Assert.True(receipt.ReceiptDate >= before);
    }

    // ---- Transactional rollback on forced failure ----

    [Fact]
    public async Task RecordReceipt_MissingCustomer_LeavesNothingChanged()
    {
        // No customer with this id exists.
        var result = await _sut.RecordReceiptAsync(new CustomerReceiptInput(9999, 50m, PaymentMode.Cash), _userId, UserRole.Owner);

        Assert.False(result.Succeeded);
        Assert.Contains("valid customer", result.Error!);
        Assert.Equal(0, await _fixture.NewContext().CustomerReceipts.CountAsync());
    }

    // ---- RBAC ----

    [Fact]
    public async Task RecordReceipt_WithoutDoBilling_IsRefused()
    {
        int customerId = AddCustomer(balance: 500m);

        // (UserRole)999 maps to no permissions.
        var result = await _sut.RecordReceiptAsync(new CustomerReceiptInput(customerId, 100m, PaymentMode.Cash), _userId, (UserRole)999);

        Assert.False(result.Succeeded);
        Assert.Contains("permission", result.Error!);
        Assert.Equal(500m, (await _fixture.NewContext().Customers.SingleAsync()).Balance);
        Assert.Equal(0, await _fixture.NewContext().CustomerReceipts.CountAsync());
    }

    [Fact]
    public async Task RecordReceipt_AsCashier_Succeeds()
    {
        int customerId = AddCustomer(balance: 500m);

        var result = await _sut.RecordReceiptAsync(new CustomerReceiptInput(customerId, 100m, PaymentMode.Cash), _userId, UserRole.Cashier);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task GetStatement_WithoutViewReports_IsRefused()
    {
        int customerId = AddCustomer(balance: 500m);

        // Cashier lacks ViewReports.
        var result = await _sut.GetStatementAsync(customerId, DateTime.Today.AddDays(-30), DateTime.Today, UserRole.Cashier);

        Assert.False(result.Succeeded);
        Assert.Contains("permission", result.Error!);
    }
}
