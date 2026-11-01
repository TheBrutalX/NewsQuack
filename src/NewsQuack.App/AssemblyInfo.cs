using System.Runtime.CompilerServices;

// T-012: expose the internal parsing seams (tolerant pass, item mapper helpers) to the App test assembly only.
[assembly: InternalsVisibleTo("NewsQuack.App.Tests")]
