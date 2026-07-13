using System.ComponentModel.DataAnnotations;

namespace InvoiceExtractor;

/// <summary>The shape we want out of the model. DataAnnotations are enforced after parsing.</summary>
internal sealed record Invoice(
    string VendorName,
    DateOnly IssuedOn,
    [property: Range(0, 1_000_000)] decimal Total);
