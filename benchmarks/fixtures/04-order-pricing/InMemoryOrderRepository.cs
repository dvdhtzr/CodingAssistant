namespace OrderPricing;

public sealed class InMemoryOrderRepository
{
    private readonly Dictionary<string, Receipt> _orders = new(StringComparer.Ordinal);

    public Receipt? Find(string orderId) =>
        _orders.TryGetValue(orderId, out Receipt? value) ? value : null;

    public void Save(Receipt receipt) =>
        _orders.Add(receipt.OrderId, receipt);
}
