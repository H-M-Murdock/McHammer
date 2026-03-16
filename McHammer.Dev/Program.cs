using McHammer.Dev;
using McHammer.Dev.Commands.Auth;
using McHammer.Dev.Commands.Info;
using McHammer.Dev.Commands.Network;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var app = new App();

app.Register(new TestAuthCommand());
app.Register(new ShowConfigCommand());
app.Register(new SyncFunctionGroupsCommand()); 
app.Register(new ManageDiscoveryCommand()); 
app.Register(new DiagnosePrtgCommand()); 

await app.RunAsync();