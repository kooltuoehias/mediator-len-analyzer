### analyzer

Roslyn-based CLI tool. It loads a .sln or .csproj file and emits JSON with DI registrations and MediatR request/handler mappings.

Run it with: `dotnet run --project analyzer -- <path-to-sln-or-csproj>`

1. The analyzer has no running state. It is not a server. It is not a background process.
2. Each time the extension needs fresh data, it starts the analyzer as a new process. It runs dotnet run --project <path> -- <solutionPath>.
3. The analyzer process runs, writes JSON to stdout, and exits. The extension reads the JSON, then the process is gone.
4. This happens at two points: when the extension activates, and when the user runs the "MediatR: Refresh Analysis" command.
5. The extension needs the analyzer PROJECT PATH, not a running instance. This path points to the folder that contains analyzer.csproj.
6. The setting mediatrLens.analyzerProjectPath holds this path. It is set once in the global VS Code user settings, so every workspace uses the same analyzer.
7. The folder at that path must exist on disk with its source code. dotnet run --project builds the analyzer from source on first run, then reuses the build cache on later runs.

So two requirements exist: the analyzer source folder must exist at the configured path, and dotnet must be installed on the machine. No persistent process is required.
