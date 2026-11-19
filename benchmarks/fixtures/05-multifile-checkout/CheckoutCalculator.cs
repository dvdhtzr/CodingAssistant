namespace MultiFileCheckout;

/// <summary>
/// Merchandise subtotal is undiscounted. Coupons reduce all merchandise once,
/// rounded at order level to cents away from zero. Tax applies only to taxable
/// merchandise after its proportional coupon discount. Shipping is excluded
/// from discounts and tax.
/// </summary>
public sealed class CheckoutCalculator
{
    public CheckoutQuote Quote(CheckoutRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Lines is null || request.Lines.Count == 0)
            throw new ArgumentException("Order has no merchandise.", nameof(request));
        if (request.Shipping < 0m || request.TaxRate is < 0m or > 1m)
            throw new ArgumentException("Invalid shipping or tax rate.", nameof(request));

        decimal subtotal = 0m, taxable = 0m;
        foreach (var line in request.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.Sku)
                    || line.Quantity <= 0 || line.UnitPrice < 0m)
                throw new ArgumentException("Invalid merchandise.", nameof(request));
            decimal lineTotal = line.Quantity * line.UnitPrice;
            subtotal += lineTotal;
            if (line.Taxable) taxable += lineTotal;
        }

        decimal rate = DiscountPolicy.Rate(request.CouponCode, request.IsMember);
        decimal discount = Money(subtotal * rate);
        // DELIBERATE DEFECT: sales tax uses taxable merchandise before the coupon.
        decimal tax = Money(taxable * request.TaxRate);
        decimal total = Money(subtotal - discount + tax + request.Shipping);
        return new CheckoutQuote(Money(subtotal), discount, tax,
                                 request.Shipping, total);
    }

    private static decimal Money(decimal amount) =>
        decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
}
