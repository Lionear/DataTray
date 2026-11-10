using DataTray.Extensions.QueryBuilder.Model;

namespace DataTray.Extensions.QueryBuilder.Tests.Generation;

// A source type the generator does not know, to prove it fails loudly instead of writing nothing.
internal sealed record UnknownSource : Source;
