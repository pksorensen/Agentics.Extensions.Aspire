using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.UserData;

/// <summary>
/// A one-shot production-data fetch displayed as its own Aspire resource. Dependants can use
/// <c>WaitForCompletion</c>, allowing the dashboard to start immediately while application
/// resources remain stopped until the snapshot is installed.
/// </summary>
public sealed class UserDataSyncResource(string name) : Resource(name), IResourceWithWaitSupport;
