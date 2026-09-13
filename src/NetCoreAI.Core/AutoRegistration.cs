using System.Reflection;

namespace NetCoreAI;

/// <summary>
/// Lets <c>AddNetCoreAI()</c> stay a one-liner: when a well-known companion assembly is present in the app
/// (because the NetCoreAI meta-package references it), its default registration method is invoked automatically.
/// Explicit calls (e.g. <c>AddSqliteStorage("custom.db")</c>) still win because they run after and replace registrations.
/// </summary>
internal static class AutoRegistration
{
    private static readonly (string Assembly, string Type, string Method)[] Defaults =
    [
        ("NetCoreAI.Storage.Sqlite", "NetCoreAI.SqliteStorageExtensions", "AddSqliteStorage"),
        ("NetCoreAI.VectorStore.Sqlite", "NetCoreAI.SqliteVectorStoreExtensions", "AddSqliteVectorStore"),
        ("NetCoreAI.Dashboard", "NetCoreAI.DashboardServiceCollectionExtensions", "AddNetCoreAIDashboard"),
        ("NetCoreAI.Documents", "NetCoreAI.DocumentsExtensions", "AddDocumentExtractors"),
    ];

    public static void Apply(NetCoreAIBuilder builder)
    {
        foreach (var (assemblyName, typeName, methodName) in Defaults)
        {
            try
            {
                var assembly = Assembly.Load(new AssemblyName(assemblyName));
                var method = assembly.GetType(typeName)?.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
                if (method is null)
                {
                    continue;
                }

                var parameters = method.GetParameters();
                var args = new object?[parameters.Length];
                args[0] = builder;
                for (var i = 1; i < parameters.Length; i++)
                {
                    args[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null;
                }

                method.Invoke(null, args);
            }
            catch (FileNotFoundException)
            {
                // package not referenced: nothing to do
            }
            catch (FileLoadException)
            {
            }
            catch (BadImageFormatException)
            {
            }
        }
    }
}
