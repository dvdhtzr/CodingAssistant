namespace OrderPricing;

/// <summary>
/// Monetary amounts are decimal and rounded to two places, away from zero.
/// SAVE10 discounts all merchandise by 10%, including taxable merchandise.
/// Sales tax applies only to taxable merchandise AFTER its share of the discount.
/// Shipping is neither discountable nor taxable. All other coupons are invalid.
/// </summary>
public sealed class PricingCalculator
{
    public PriceQuote Calculate(OrderRequest order, decimal salesTaxRate)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (order.Lines is null || order.Lines.Count == 0)
            throw new ArgumentException("An order needs at least one item.", nameof(order));
        if (salesTaxRate < 0m || salesTaxRate > 1m)
            throw new ArgumentOutOfRangeException(nameof(salesTaxRate));
        if (order.Shipping < 0m)
            throw new ArgumentException("Shipping cannot be negative.", nameof(order));

        decimal discountRate = (order.CouponCode ?? "").Trim() switch
        {
            "" => 0m,
            "SAVE10" => 0.10m,
            _ => throw new ArgumentException("Unknown coupon.", nameof(order))
        };

        decimal subtotal = 0m;
        decimal taxableSubtotal = 0m;
        foreach (OrderLine line in order.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.Sku) || line.Quantity <= 0 || line.UnitPrice < 0m)
                throw new ArgumentException("Invalid order line.", nameof(order));
            decimal lineTotal = line.UnitPrice * line.Quantity;
            subtotal += lineTotal;
            if (line.IsTaxable)
                taxableSubtotal += lineTotal;
        }

        decimal discount = Money(subtotal * discountRate);
        decimal tax = Money(taxableSubtotal * salesTaxRate);
        decimal total = Money(subtotal - discount + tax + order.Shipping);

        return new PriceQuote(Money(subtotal), discount, tax, order.Shipping, total);
    }

    private static decimal Money(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}
