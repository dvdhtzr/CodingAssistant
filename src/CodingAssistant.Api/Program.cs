using CodingAssistant.Api;

// Visual Studio F5 only builds the API startup project by default.
// Ensure the checked-out React frontend is current BEFORE static file
// providers are initialized; never fall back to an outdated UI.
_ = WindowsDevelopmentFrontend.EnsureCurrent();

var app = ApiHost.Build(args);
await app.RunAsync();
