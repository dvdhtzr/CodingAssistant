namespace MultiFileCheckout;

public sealed record CheckoutLine(string Sku, int Quantity, decimal UnitPrice,
                                  bool Taxable);
public sealed record CheckoutRequest(IReadOnlyList<CheckoutLine> Lines,
                                     string? CouponCode, bool IsMember,
                                     decimal Shipping, decimal TaxRate);
public sealed record CheckoutQuote(decimal Subtotal, decimal Discount,
                                   decimal Tax, decimal Shipping, decimal Total);
