using SocialSimulation.Core;

namespace SocialSimulation.UnityRuntime
{
    /// <summary>Supplies the validated content contract before scene instances are registered.</summary>
    public interface ISocialContentProvider
    {
        ContentRegistry BuildRegistry();
    }
}
