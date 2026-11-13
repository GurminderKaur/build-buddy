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

`--verify` applies the model's patched file to a temp copy and rebuilds it. A result is called **fixed** only when `build_passes` is yes (the patched project rebuilds with no compiler errors). Gemma 3 4B, running locally, fixed 6 of 6 single-error samples; each patched project rebuilt cleanly. That `--all --verify` run averaged 11.0 s per error (6.1–21.7 s). Those are six easy, one-error samples — a start, not proof that it fixes C# errors every time.

**Update (behaviour checks, [issue #1](https://github.com/GurminderKaur/build-buddy/issues/1)):** building isn't enough. Some fixes compiled but changed what the program prints, so `--verify` now also runs the patched program and compares its output with the sample's `expected.txt`. A fix only counts as **fixed** if it builds **and** behaves. Replaying Gemma's Nov 9 answers through the new check: 6 of 6 build, but only 4 of 6 behave (cs0161 printed three lines instead of one; cs0103 printed an empty line).

## Requirements

- [.NET SDK](https://dotnet.microsoft.com/download) 10 (the project targets `net10.0`)
- [Ollama](https://ollama.com) running locally
- A local model, for example:

```
ollama pull gemma3:4b
```

## Usage

```
dotnet run --project src/BuildBuddy -- <path-to-project-or-folder>
dotnet run --project src/BuildBuddy -- <path-to-project-or-folder> --verify
dotnet run --project src/BuildBuddy -- samples --all --verify
```

`--verify` copies the target (skipping `bin/` and `obj/`), overwrites the failing file with the model's complete corrected source, rebuilds the copy, then deletes it. If the build passes and the sample has an `expected.txt`, it also runs the patched program (30 s limit) and compares its output with `expected.txt`, ignoring line-ending style and trailing spaces. `--all` runs that on every subfolder and prints one combined summary.

Results append to `results/results-v2.csv`:

| Column | Meaning |
|---|---|
| `build_passes` | The patched project builds with no compiler errors |
| `behaves` | `yes`/`no` when the sample has `expected.txt` and the build passed; `n/a` otherwise |
| `fixed` | `yes` only if it builds **and** `behaves` is not `no` |

Before timing anything, Build Buddy loads the model once and records that cold-start time separately in `results/warmup.csv`, so the first error no longer pays for loading the model. If Ollama fails, that error is recorded as not fixed and the run continues.

**Prompt experiment: `--keep-output`.** Adds one rule to the verify prompt: *"Keep the program's behaviour and console output exactly the same: do not add, remove, or change any output statements."* It is off by default, so the baseline prompt stays reproducible, and its results go to `results/results-v2-keep-output.csv`. Suggested by a Day 2 reader: at temperature 0 the CS0161 failure repeats every run, which makes it a clean test of one prompt change.

```
dotnet run --project src/BuildBuddy -- samples --all --verify --keep-output
```

`results/results.csv` holds the earlier build-only runs (Nov 6–9) in their original format, unchanged, because the Day 1–3 posts and the Tiger Data import use it.

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
6. With `--verify`, rebuilds a patched copy, then runs it and compares its output with `expected.txt`. It records `fixed` only if the rebuild is clean and the output matches.

No NuGet packages are needed. It talks to Ollama with plain `HttpClient`.

## Roadmap

- [x] Benchmark set of broken sample projects, one per common error code
- [x] Automatic check: apply the suggested fix and see whether the build passes
- [x] Behaviour check: run the fixed program and compare its output with `expected.txt` ([issue #1](https://github.com/GurminderKaur/build-buddy/issues/1))
- [x] Time the model's cold start separately from each fix
- [ ] Rerun the full benchmark with the live model and behaviour checks
- [ ] Accuracy and timing table across local models
- [ ] Package as a `dotnet` global tool