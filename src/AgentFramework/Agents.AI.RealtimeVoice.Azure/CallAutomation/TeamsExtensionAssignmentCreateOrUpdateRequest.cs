namespace Agents.AI.RealtimeVoice.Azure.CallAutomation;

public class TeamsExtensionAssignmentCreateOrUpdateRequest
{
    public TeamsExtensionAssignmentCreateOrUpdateRequest(string principalType, List<string>? clientIds = null)
    {
        PrincipalType = principalType;
        ClientIds = clientIds ?? [];
    }
    /// <summary> Initializes a new instance of <see cref="TeamsExtensionAssignmentCreateOrUpdateRequest"/>. </summary>
    /// <param name="principalType"> The type of principal the assignment is for. </param>
    public TeamsExtensionAssignmentCreateOrUpdateRequest(TeamsExtensionPrincipalType principalType)
    {
        PrincipalType = principalType.Value;
        ClientIds = new List<string>();
    }

    /// <summary> Initializes a new instance of <see cref="TeamsExtensionAssignmentCreateOrUpdateRequest"/>. </summary>
    /// <param name="principalType"> The type of principal the assignment is for. </param>
    /// <param name="clientIds"></param>
    public TeamsExtensionAssignmentCreateOrUpdateRequest(TeamsExtensionPrincipalType principalType, IList<string> clientIds)
    {
        PrincipalType = principalType.Value;
        ClientIds = clientIds;
    }

    /// <summary> The type of principal the assignment is for. </summary>
    public string PrincipalType { get; set; }
    /// <summary> Gets the client ids. </summary>
    public IList<string> ClientIds { get; }
}
public partial class TeamsExtensionAssignmentResponse
{
    public TeamsExtensionAssignmentResponse() { }
    /// <summary> Initializes a new instance of <see cref="TeamsExtensionAssignmentResponse"/>. </summary>
    /// <param name="objectId"></param>
    /// <param name="tenantId"></param>
    /// <param name="principalType"> The type of principal the assignment is for. </param>
    /// <exception cref="ArgumentNullException"> <paramref name="objectId"/> or <paramref name="tenantId"/> is null. </exception>
    public TeamsExtensionAssignmentResponse(string objectId, string tenantId, TeamsExtensionPrincipalType principalType)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(objectId, nameof(objectId));
        ArgumentNullException.ThrowIfNullOrEmpty(tenantId, nameof(tenantId));

        ObjectId = objectId;
        TenantId = tenantId;
        PrincipalType = principalType;
        ClientIds = new List<string>();
    }

    /// <summary> Initializes a new instance of <see cref="TeamsExtensionAssignmentResponse"/>. </summary>
    /// <param name="objectId"></param>
    /// <param name="tenantId"></param>
    /// <param name="principalType"> The type of principal the assignment is for. </param>
    /// <param name="clientIds"></param>
    public TeamsExtensionAssignmentResponse(string objectId, string tenantId, TeamsExtensionPrincipalType principalType, IReadOnlyList<string> clientIds)
    {
        ObjectId = objectId;
        TenantId = tenantId;
        PrincipalType = principalType;
        ClientIds = clientIds;
    }

    /// <summary> Gets the object id. </summary>
    public string? ObjectId { get; }
    /// <summary> Gets the tenant id. </summary>
    public string? TenantId { get; }
    /// <summary> The type of principal the assignment is for. </summary>
    public TeamsExtensionPrincipalType? PrincipalType { get; }
    /// <summary> Gets the client ids. </summary>
    public IReadOnlyList<string>? ClientIds { get; }
}
public class TeamsExtensionPrincipalType
{
    public string Value { get; }

    /// <summary> Initializes a new instance of <see cref="TeamsExtensionPrincipalType"/>. </summary>
    /// <exception cref="ArgumentNullException"> <paramref name="value"/> is null. </exception>
    public TeamsExtensionPrincipalType(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    private const string ResourceAccountValue = "teamsResourceAccount";
    private const string UserValue = "user";

    /// <summary> resourceAccount. </summary>
    public static TeamsExtensionPrincipalType TeamsResourceAccount { get; } = new TeamsExtensionPrincipalType(ResourceAccountValue);
    /// <summary> user. </summary>
    public static TeamsExtensionPrincipalType User { get; } = new TeamsExtensionPrincipalType(UserValue);
    public override string ToString() => Value;

}
