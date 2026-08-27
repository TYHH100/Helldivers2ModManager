namespace Helldivers2ModManager.Frontend.Navigation;

public sealed record FrontendRoute(
    string Key,
    string Group,
    string TitleKey,
    string DescriptionKey,
    Type ViewModelType,
    bool IsDiagnostic = false);
