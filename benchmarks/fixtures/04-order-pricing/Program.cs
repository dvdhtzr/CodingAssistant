using OrderPricing;

// Independent executable acceptance harness: no NuGet/test framework needed.
// The assistant edits PricingCalculator.cs, never this file.
int checks = 0;
var failures = new List<string>();

void Check(string name, Action assertion)
{
    checks++;
    try
    {
        assertion();
        Console.WriteLine($"PASS: {name}");
    }
    catch (Exception exception)
    {
        failures.Add(name);
        Console.WriteLine($"FAIL: {name}: {exception.GetType().Name}: {exception.Message}");
    }
}

static void Equal(decimal expected, decimal actual, string label)
{
    if (expected != actual)
        throw new InvalidOperationException($"{label}: expected {expected:F2}, got {actual:F2}");
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}");
}

var calculator = new PricingCalculator();

OrderRequest Mixed(string id, string? coupon = "SAVE10", decimal shipping = 5m) =>
    new(id,
        [new OrderLine("COFFEE", 2, 19.99m, true),
         new OrderLine("GIFT-CARD", 1, 15.00m, false)],
        coupon, shipping);

Check("mixed taxable / exempt merchandise with SAVE10", () =>
{
    PriceQuote quote = calculator.Calculate(Mixed("A"), 0.0875m);
    Equal(54.98m, quote.Subtotal, "subtotal");
    Equal(5.50m, quote.Discount, "discount");
    Equal(3.15m, quote.Tax, "tax after taxable discount");
    Equal(5.00m, quote.Shipping, "shipping");
    Equal(57.63m, quote.Total, "total");
});

Check("no coupon leaves the taxable base unchanged", () =>
{
    PriceQuote quote = calculator.Calculate(Mixed("B", null), 0.0875m);
    Equal(0m, quote.Discount, "discount");
    Equal(3.50m, quote.Tax, "tax");
    Equal(63.48m, quote.Total, "total");
});

Check("exempt merchandise gets discounted but never taxed", () =>
{
    var order = new OrderRequest("C",
        [new OrderLine("BOOK", 2, 9.99m, false)], "SAVE10", 4.95m);
    PriceQuote quote = calculator.Calculate(order, 0.0875m);
    Equal(19.98m, quote.Subtotal, "subtotal");
    Equal(2.00m, quote.Discount, "discount");
    Equal(0m, quote.Tax, "tax");
    Equal(22.93m, quote.Total, "total");
});

Check("money rounds away from zero at the cent", () =>
{
    var order = new OrderRequest("D",
        [new OrderLine("STICKER", 1, 5.05m, true)], "SAVE10", 0m);
    PriceQuote quote = calculator.Calculate(order, 0.10m);
    Equal(0.51m, quote.Discount, "discount rounding");
    Equal(0.45m, quote.Tax, "discounted tax rounding");
    Equal(4.99m, quote.Total, "total");
});

Check("shipping stays outside the tax and coupon bases", () =>
{
    var order = new OrderRequest("E",
        [new OrderLine("WIDGET", 1, 10m, true)], "SAVE10", 100m);
    PriceQuote quote = calculator.Calculate(order, 0.10m);
    Equal(1m, quote.Discount, "discount");
    Equal(0.90m, quote.Tax, "tax");
    Equal(109.90m, quote.Total, "total");
});

Check("invalid tax rate is rejected", () =>
    Throws<ArgumentOutOfRangeException>(() =>
        calculator.Calculate(Mixed("F"), -0.01m)));

Check("unknown coupon is rejected", () =>
    Throws<ArgumentException>(() =>
        calculator.Calculate(Mixed("G", "BOGUS"), 0.08m)));

Check("zero quantity is rejected", () =>
    Throws<ArgumentException>(() =>
        calculator.Calculate(new OrderRequest("H",
            [new OrderLine("BAD", 0, 5m, true)], null, 0m), 0.08m)));

Check("order service persists its correctly priced receipt", () =>
{
    var repository = new InMemoryOrderRepository();
    var service = new OrderService(calculator, repository);
    Receipt receipt = service.Place(Mixed("I"), 0.0875m);
    Equal(57.63m, receipt.Price.Total, "receipt total");
    if (!ReferenceEquals(receipt, repository.Find("I")))
        throw new InvalidOperationException("Receipt not persisted.");
});

Check("repeated order ID returns the original receipt", () =>
{
    var service = new OrderService(calculator, new InMemoryOrderRepository());
    Receipt first = service.Place(Mixed("J", null), 0.0875m);
    Receipt repeated = service.Place(Mixed("J", "SAVE10"), 0.0875m);
    if (!ReferenceEquals(first, repeated))
        throw new InvalidOperationException("An existing order was repriced.");
    Equal(63.48m, repeated.Price.Total, "original receipt");
});

Console.WriteLine($"ORDER-PRICING ACCEPTANCE: {checks - failures.Count}/{checks} checks passed");
if (failures.Count != 0)
{
    Console.Error.WriteLine("ORDER-PRICING ACCEPTANCE FAILED");
    return 1;
}
Console.WriteLine("ORDER-PRICING ACCEPTANCE PASS");
return 0;
