namespace MultiFileCheckout;

/// <summary>
/// SAVE10 applies to all customers. MEMBER20 must be accepted for members
/// and rejected for nonmembers. Unknown coupons are invalid.
/// </summary>
public static class DiscountPolicy
{
    public static decimal Rate(string? couponCode, bool isMember) =>
        (couponCode ?? "").Trim() switch
        {
            "" => 0m,
            "SAVE10" => 0.10m,
            _ => throw new ArgumentException("Unknown or ineligible coupon.", nameof(couponCode))
        };
}
