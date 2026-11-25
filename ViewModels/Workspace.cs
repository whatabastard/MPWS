using MPWS.Models;
using MPWS.Services;

namespace MPWS.ViewModels;
public sealed class Workspace
{
    public List<Finding> Results { get; set; } = [];
    public DateTime? LastScan { get; set; }
    public IEnumerable<Article> Search(string category, string query) =>
        KnowledgeBaseService.All.Where(a => (category == "Все" || category == a.App)
            && (a.Title + " " + a.App + " " + a.Tags + " " + string.Join(" ", a.Steps))
                .Contains(query.Trim(), StringComparison.OrdinalIgnoreCase));
}
