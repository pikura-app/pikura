# Contributing to Pikura

Thank you for your interest in contributing! This document explains how to get involved.

## Table of Contents

- [Code of Conduct](#code-of-conduct)
- [How to Report a Bug](#how-to-report-a-bug)
- [How to Request a Feature](#how-to-request-a-feature)
- [Development Setup](#development-setup)
- [Submitting a Pull Request](#submitting-a-pull-request)
- [Coding Style](#coding-style)
- [Commit Messages](#commit-messages)

---

## Code of Conduct

This project follows the [Contributor Covenant Code of Conduct](CODE_OF_CONDUCT.md). By participating you agree to abide by its terms.

---

## How to Report a Bug

1. **Search existing issues** first — someone may have already reported it.
2. Open a [Bug Report](.github/ISSUE_TEMPLATE/bug_report.md) and fill in all sections.
3. For security vulnerabilities, **do not open a public issue** — see [SECURITY.md](SECURITY.md).

---

## How to Request a Feature

1. Check [existing issues](https://github.com/pikura-app/pikura/issues) and open ones.
2. Open a [Feature Request](.github/ISSUE_TEMPLATE/feature_request.md) and describe the use-case.

---

## Development Setup

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Windows, macOS, or Linux
- Git

### Build

```bash
git clone https://github.com/pikura-app/pikura.git
cd pikura
dotnet build src/Pikura.Avalonia/Pikura.Avalonia.csproj -c Debug
```

### Run

```bash
dotnet run --project src/Pikura.Avalonia/Pikura.Avalonia.csproj -c Debug
```

### Project Structure

| Project | Purpose |
|---------|---------|
| `Pikura.Avalonia` | UI layer (Avalonia, ViewModels, Views) |
| `Pikura.Core` | Business logic, Pixiv client, download pipeline, settings |
| `Pikura.Agent` | Background agent process for scheduled downloads |

---

## Submitting a Pull Request

1. **Fork** the repository and create a branch from `main`.
2. Make your changes — keep the scope focused.
3. Ensure the project builds with no new errors: `dotnet build`.
4. Open a PR against `main` using the [PR template](.github/PULL_REQUEST_TEMPLATE.md).
5. Fill in all sections of the template, including testing steps.

### Branch naming

| Type | Pattern | Example |
|------|---------|---------|
| Bug fix | `fix/<short-description>` | `fix/followed-artists-pagination` |
| Feature | `feat/<short-description>` | `feat/scheduled-downloads-ui` |
| Docs | `docs/<short-description>` | `docs/update-contributing` |

---

## Coding Style

- **C# conventions** — follow existing patterns in the file you're editing.
- **No trailing whitespace**, LF line endings preferred (`.gitattributes` enforces this on push).
- **No new comments or doc-comments** unless you're documenting a non-obvious decision.
- **ViewModels** use CommunityToolkit.Mvvm `[ObservableProperty]` / `[RelayCommand]`.
- **Logging** — use the injected `Logger` (from `ViewModelBase`) for `INF`/`WRN`/`ERR`. Never use `Console.WriteLine` or `Debug.WriteLine` in production paths.

---

## Commit Messages

Use the [Conventional Commits](https://www.conventionalcommits.org/) format:

```
<type>: <short description>

[optional body]
```

| Type | When to use |
|------|-------------|
| `fix` | Bug fix |
| `feat` | New feature |
| `chore` | Build, CI, dependency updates |
| `docs` | Documentation only |
| `refactor` | Code change that neither fixes a bug nor adds a feature |
| `perf` | Performance improvement |

---

## Licensing of contributions

Pikura is licensed under the GNU GPL v3.0 and is also offered under separate commercial licenses by its author. To keep that possible, by submitting a contribution (pull request, patch, or other material) you agree that:

1. Your contribution is licensed to the public under the GPL v3.0, and
2. You grant mrplusultra a perpetual, worldwide, non-exclusive, royalty-free, irrevocable license to use, modify, sublicense, and relicense your contribution under any terms, including proprietary/commercial licenses.

You confirm that you have the right to make the contribution and grant these rights. If you can't agree to this, please open an issue instead of a pull request.
