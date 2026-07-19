using System.Windows.Media;

namespace IBTM.Application.Abstractions;

public sealed record InspectionOutcome(InspectionResult Result, ImageSource Image);

public interface IInspectionService
{
    Task<InspectionOutcome> InspectAsync(CancellationToken cancellationToken = default);
}
