using System.Threading.Tasks;

namespace Magi.UnityTools.Core
{
    /// <summary>
    /// Optional async initialization contract for services.
    /// </summary>
    public interface IInitializable
    {
        Task<Result> InitializeAsync();
    }
}
