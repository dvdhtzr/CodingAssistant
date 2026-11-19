namespace OrderPricing;

public sealed record OrderLine(string Sku, int Quantity, decimal UnitPrice, bool IsTaxable);

public sealed record OrderRequest(
    string OrderId,
    IReadOnlyList<OrderLine> Lines,
    string? CouponCode,
    decimal Shipping);

public sealed record PriceQuote(
    decimal Subtotal,
    decimal Discount,
    decimal Tax,
    decimal Shipping,
    decimal Total);

public sealed record Receipt(string OrderId, PriceQuote Price);
