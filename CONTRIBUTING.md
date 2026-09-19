# Contributing to DiskEye

Thank you for your interest in contributing to DiskEye! This document provides guidelines and information for contributors.

## Code of Conduct

This project adheres to a simple code of conduct:
- Be respectful and constructive
- Focus on the technical aspects
- Help others learn and grow

## How to Contribute

### Reporting Bugs

Before creating bug reports, please check the issue list as you might find out that you don't need to create one. When you are creating a bug report, please include as many details as possible:

- **Use a clear and descriptive title**
- **Describe the exact steps to reproduce the problem**
- **Provide specific examples**
- **Describe the behavior you observed and what behavior you expected**
- **Include screenshots if applicable**
- **Include your environment details** (Windows version, .NET version, etc.)

### Suggesting Enhancements

Enhancement suggestions are tracked as GitHub issues. When creating an enhancement suggestion, please include:

- **Use a clear and descriptive title**
- **Provide a step-by-step description of the suggested enhancement**
- **Provide specific examples to demonstrate the steps**
- **Describe the current behavior and explain which behavior you expected to see instead**
- **Explain why this enhancement would be useful**

### Pull Requests

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Make your changes
4. Test your changes thoroughly
5. Commit your changes (`git commit -m 'Add some amazing feature'`)
6. Push to the branch (`git push origin feature/amazing-feature`)
7. Open a Pull Request

## Development Setup

### Prerequisites

- Windows 10/11 (64-bit)
- .NET 8.0 SDK
- Visual Studio 2022 or Visual Studio Code
- Git

### Getting Started

1. Clone your fork:
   ```bash
   git clone https://github.com/YOUR_USERNAME/disk-eye.git
   cd disk-eye
   ```

2. Open the project:
   - Visual Studio: Open `src/DiskEye/DiskEye.csproj`
   - VS Code: Open the folder and install recommended extensions

3. Build the project:
   ```bash
   dotnet build src/DiskEye/DiskEye.csproj
   ```

4. Run as Administrator (required for ETW):
   ```bash
   dotnet run --project src/DiskEye/DiskEye.csproj
   ```

## Code Style

### C# Guidelines

- Follow the [C# Coding Conventions](https://docs.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions)
- Use meaningful variable and method names
- Add XML documentation for public APIs
- Keep methods focused and concise
- Use `var` when the type is obvious

### Naming Conventions

- **Classes/Structs**: PascalCase (e.g., `EventAggregator`)
- **Methods**: PascalCase (e.g., `RefreshData`)
- **Properties**: PascalCase (e.g., `MonitoredDrives`)
- **Private fields**: _camelCase (e.g., `_refreshTimer`)
- **Local variables**: camelCase (e.g., `savedScroll`)
- **Constants**: PascalCase (e.g., `PipeName`)

### Code Formatting

- Use 4 spaces for indentation (no tabs)
- Keep lines under 120 characters when possible
- Use blank lines to separate logical sections
- Align related statements

## Testing

### Running Tests

```bash
dotnet test src/DiskEye.Tests/DiskEye.Tests.csproj
```

### Writing Tests

- Write unit tests for new features
- Test both success and failure cases
- Use descriptive test names
- Follow the Arrange-Act-Assert pattern

## Documentation

### Code Documentation

- Add XML documentation comments for all public APIs
- Include parameter descriptions and return value descriptions
- Provide usage examples when helpful

### User Documentation

- Update README.md for user-facing changes
- Add screenshots for UI changes
- Update the user guide for new features

## Commit Messages

Follow the [Conventional Commits](https://www.conventionalcommits.org/) specification:

```
<type>(<scope>): <description>

[optional body]

[optional footer(s)]
```

Types:
- **feat**: A new feature
- **fix**: A bug fix
- **docs**: Documentation only changes
- **style**: Changes that do not affect the meaning of the code
- **refactor**: A code change that neither fixes a bug nor adds a feature
- **perf**: A code change that improves performance
- **test**: Adding missing tests or correcting existing tests
- **chore**: Changes to the build process or auxiliary tools

Examples:
```
feat(ui): add dark mode support
fix(etw): resolve crash when process exits quickly
docs: update README with new features
```

## Pull Request Process

1. **Update documentation** if needed
2. **Add tests** for new features
3. **Ensure all tests pass**
4. **Update CHANGELOG.md** with your changes
5. **Request review** from maintainers

## Issue Labels

- **bug**: Something isn't working
- **enhancement**: New feature or request
- **documentation**: Improvements or additions to documentation
- **good first issue**: Good for newcomers
- **help wanted**: Extra attention is needed
- **question**: Further information is requested

## Questions?

Feel free to open an issue with the `question` label if you have any questions about contributing.

## License

By contributing to DiskEye, you agree that your contributions will be licensed under the MIT License.

Thank you for contributing to DiskEye! 🎉
