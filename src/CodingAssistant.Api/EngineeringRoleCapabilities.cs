namespace CodingAssistant.Api;

/// <summary>Host-owned capability vocabulary. No capability grants execution,
/// owner approval, deployment or Git privileges. Existing admission gates
/// remain mandatory; this policy can only further restrict an action.</summary>
public enum EngineeringCapability
{
    ProposeRequirements,
    ProposeArchitecture,
    ScheduleWork,
    WriteSource,
    ReviewCode,
    ReviewAcceptance,
    SendMessage
}

public static class EngineeringRoleCapabilities
{
    public static bool Allows(string role, EngineeringCapability capability) =>
        role switch
        {
            "ProductOwner" => capability is EngineeringCapability.ProposeRequirements
                or EngineeringCapability.SendMessage,
            "SolutionArchitect" => capability is EngineeringCapability.ProposeArchitecture
                or EngineeringCapability.SendMessage,
            "TechnicalLead" => capability is EngineeringCapability.ScheduleWork
                or EngineeringCapability.SendMessage,
            "SoftwareEngineer" => capability is EngineeringCapability.WriteSource
                or EngineeringCapability.SendMessage,
            "LeadSoftwareEngineer" => capability is EngineeringCapability.ReviewCode
                or EngineeringCapability.SendMessage,
            "QaEngineer" => capability is EngineeringCapability.ReviewAcceptance
                or EngineeringCapability.SendMessage,
            _ => false
        };

    public static void Require(string role, EngineeringCapability capability)
    {
        if (!Allows(role, capability))
            throw new StoreFailure("AGENT_CAPABILITY_DENIED");
    }
}
