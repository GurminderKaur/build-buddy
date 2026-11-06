# Build Buddy

An offline explainer for C# build errors. Build Buddy runs `dotnet build`, catches the compiler errors (CS0246, CS1061, and friends), and asks a **local** model to explain each one in plain English and suggest the smallest fix. Your code never leaves your machine.

Built during [MLH Global Hack Week: Builders Week](https://events.mlh.com/events/14729-global-hack-week-builders-week) (November 6–12, 2026).

## Example

```
> dotnet run --project src/BuildBuddy -- samples/cs0246-missing-using
Building samples/cs0246-missing-using ...
Found 1 error(s). Asking gemma3:4b ...

CS0246  Program.cs:1  The type or namespace name 'StringBuilder' could not be found
The error occurs because the `StringBuilder` class is not explicitly imported into
the current scope. C# requires you to bring in the necessary namespaces to use
classes like `StringBuilder`.

    using System.Text; // Add this line

(22.2s with gemma3:4b)
```

That first run (22.2 s) includes loading the model into memory.

`--verify` applies the model's patched file to a temp copy and rebuilds it. A result is called **fixed** only when `build_passes` is yes (the patched project rebuilds with no compiler errors). Gemma 3 4B, running locally, fixed 6 of 6 single-error samples; each patched project rebuilt cleanly. Warm runs took 13.5–18.5 s per error. Those are six easy, one-error samples — a start, not proof that it fixes C# errors every time.

## Requirements

- [.NET SDK](https://dotnet.microsoft.com/download) 8 or later
- [Ollama](https://ollama.com) running locally
- A local model, for example:

```
ollama pull gemma3:4b
```

## Usage

```
dotnet run --project src/BuildBuddy -- <path-to-project-or-folder>
dotnet run --project src/BuildBuddy -- <path-to-project-or-folder> --verify
```

`--verify` copies the target (skipping `bin/` and `obj/`), overwrites the failing file with the model's complete corrected source, rebuilds the copy, then deletes it. Results append to `results/results.csv`. The `fixed` column is `yes` only when `build_passes` is `yes`.

Optional environment variables:

| Variable | Default | Purpose |
|---|---|---|
| `BUILDBUDDY_MODEL` | `gemma3:4b` | Which Ollama model to ask |
| `OLLAMA_HOST` | `http://localhost:11434` | Where Ollama is running |

The exit code is `0` when the build has no compiler errors and `1` when it does.

## How it works

1. Runs `dotnet build` on the target and captures the output.
2. Parses every `error CS####` line and removes the duplicates MSBuild sometimes prints.
3. Reads a few lines of source around each error.
4. Sends the error code, message, and code snippet to the local model at temperature 0.
5. Prints the explanation, the suggested fix, and how long the model took.
6. With `--verify`, rebuilds a patched copy and records `build_passes` / `fixed` only if that rebuild is clean.

No NuGet packages are needed. It talks to Ollama with plain `HttpClient`.

## Roadmap

- [x] Benchmark set of broken sample projects, one per common error code
- [x] Automatic check: apply the suggested fix and see whether the build passes
- [ ] Accuracy and timing table across local models
- [ ] Package as a `dotnet` global tool