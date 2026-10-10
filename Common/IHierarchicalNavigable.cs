using System.Threading.Tasks;

namespace Journal.Common
{
    public interface IHierarchicalNavigable
    {
        Task<bool> TryHandleNavigateBackAsync();
        Task<bool> TryHandleNavigateForwardAsync();
    }
}
