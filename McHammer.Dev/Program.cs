using McHammer.Dev;
using McHammer.Dev.Commands.Auth;
using McHammer.Dev.Commands.Info;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var app = new App();

// ── Commands registrieren ──────────────────────────────
app.Register(new TestAuthCommand());
app.Register(new ShowConfigCommand());
// weitere Commands hier einfach hinzufügen

// ──────────────────────────────────────────────────────
await app.RunAsync();