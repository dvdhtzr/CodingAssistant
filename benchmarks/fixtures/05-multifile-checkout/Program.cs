using MultiFileCheckout;

// Owner-controlled independent executable contract; never included in the
// assistant's read-only neighbor discovery and never selected for editing.
int totalChecks = 0;
int passedChecks = 0;

void Check(string name, Action action)
{
    totalChecks++;
    try
    {
        action();
        passedChecks++;
        Console.WriteLine($"PASS: {name}");
    }
    catch (Exception exception)
    {
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

CheckoutRequest Mixed(string? coupon, bool member = false, decimal shipping = 2m) =>
    new(
        [new CheckoutLine("LAMP", 1, 50m, true),
         new CheckoutLine("BOOK", 1, 20m, false)],
        coupon, member, shipping, 0.10m);

var calculator = new CheckoutCalculator();

Check("SAVE10 discounts full merchandise and reduced taxable tax", () =>
{
    var quote = calculator.Quote(Mixed("SAVE10"));
    Equal(70m, quote.Subtotal, "subtotal");
    Equal(7m, quote.Discount, "discount");
    Equal(4.50m, quote.Tax, "tax");
    Equal(69.50m, quote.Total, "total");
});

Check("MEMBER20 applies only to eligible member across full merchandise", () =>
{
    var quote = calculator.Quote(Mixed("MEMBER20", member: true));
    Equal(70m, quote.Subtotal, "subtotal");
    Equal(14m, quote.Discount, "member discount");
    Equal(4m, quote.Tax, "taxable after member discount");
    Equal(62m, quote.Total, "total");
});

Check("MEMBER20 discounts exempt merchandise but never taxes it", () =>
{
    var order = new CheckoutRequest(
        [new CheckoutLine("BOOK", 1, 20m, false)], "MEMBER20", true, 0m, 0.10m);
    var quote = calculator.Quote(order);
    Equal(20m, quote.Subtotal, "subtotal");
    Equal(4m, quote.Discount, "discount");
    Equal(0m, quote.Tax, "tax");
    Equal(16m, quote.Total, "total");
});

Check("MEMBER20 refuses a nonmember", () =>
    Throws<ArgumentException>(() => calculator.Quote(Mixed("MEMBER20"))));

Check("no coupon preserves original taxable base", () =>
{
    var quote = calculator.Quote(Mixed(null));
    Equal(0m, quote.Discount, "discount");
    Equal(5m, quote.Tax, "tax");
    Equal(77m, quote.Total, "total");
});

Check("unknown coupon rejected", () =>
    Throws<ArgumentException>(() => calculator.Quote(Mixed("INVALID"))));

Check("invalid line quantity rejected", () =>
{
    var request = new CheckoutRequest(
        [new CheckoutLine("BAD", 0, 1m, true)], null, false, 0m, 0.10m);
    Throws<ArgumentException>(() => calculator.Quote(request));
});

Check("round the discount once across taxable and exempt categories", () =>
{
    var request = new CheckoutRequest(
        [new CheckoutLine("TAXABLE", 1, 0.05m, true),
         new CheckoutLine("EXEMPT", 1, 0.05m, false)],
        "SAVE10", false, 0m, 0.10m);
    var quote = calculator.Quote(request);
    Equal(0.10m, quote.Subtotal, "subtotal");
    Equal(0.01m, quote.Discount, "order-wide discount");
    Equal(0m, quote.Tax, "tax after discounted taxable portion");
    Equal(0.09m, quote.Total, "total");
});

Check("shipping excluded from tax and coupon bases", () =>
{
    var request = new CheckoutRequest(
        [new CheckoutLine("WIDGET", 1, 10m, true)],
        "SAVE10", false, 100m, 0.10m);
    var quote = calculator.Quote(request);
    Equal(1m, quote.Discount, "discount");
    Equal(0.90m, quote.Tax, "tax");
    Equal(109.90m, quote.Total, "total");
});

Console.WriteLine($"MULTI-FILE CHECKOUT: {passedChecks}/{totalChecks} checks passed");
if (passedChecks != totalChecks)
{
    Console.Error.WriteLine("MULTI-FILE CHECKOUT FAILED");
    return 1;
}
Console.WriteLine("MULTI-FILE CHECKOUT PASS");
return 0;
