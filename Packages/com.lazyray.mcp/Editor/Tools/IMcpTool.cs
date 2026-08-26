using Newtonsoft.Json.Linq;

namespace LazyRay.Tools
{
    public interface IMcpTool
    {
        string Name { get; }
        string Description { get; }
        JObject InputSchema { get; }
        string Execute(JObject input);
        bool IsDestructive { get; }

        /// <summary>
        /// v6.4: True if Execute() touches no main-thread-only Unity APIs and
        /// can run directly on the pipe thread. Thread-safe tools get zero
        /// dispatcher latency, never return BUSY, and keep working during
        /// compiles/imports. Default false — tools must opt in explicitly.
        /// </summary>
        bool IsThreadSafe => false;
    }
}
