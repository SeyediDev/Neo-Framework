using Neo.Domain.Entities.Common;

namespace Neo.Domain.Repository;
public interface ICultureTermQueryRepository : IQueryRepository<CultureTerm, int>
{
    Task<Dictionary<int, List<CultureTerm>>> GetTerms(string subjectTitle, List<int> subjectIds, CancellationToken cancellationToken);
    Task<Dictionary<int, List<CultureTerm>>> GetTerms(string subjectTitle, string subjectField, List<int> subjectIds, CancellationToken cancellationToken);
}