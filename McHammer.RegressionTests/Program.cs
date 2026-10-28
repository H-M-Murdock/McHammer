using System.Net;
using System.Reflection;
using System.Text;
using McHammer.Lib.Configuration;
using McHammer.Lib.Exceptions;
using McHammer.Lib.Services.Network;

// Regression tests use fake HttpMessageHandlers only; never contact PRTG.
await Check("JSON numeric ID", "{\"id\":42}", "[]", false, 42);
await Check("JSON string ID", "{\"id\":\"42\"}", "[]", false, 42);
await Check("201 empty body", "", "[{\"objid\":42,\"name\":\"NewGroup\",\"tags\":\"\"}]", false, 42);
await Check("204 whitespace body", "  ", "[{\"objid\":42,\"name\":\"NewGroup\",\"tags\":\"\"}]", false, 42, HttpStatusCode.NoContent);
await Check("JSON without ID", "{}", "[{\"objid\":42,\"name\":\"NewGroup\",\"tags\":\"\"}]", false, 42);
await Check("Delayed visibility", "", "[{\"objid\":42,\"name\":\"NewGroup\",\"tags\":\"\"}]", false, 42, emptyReads: 1);
await Check("Duplicate exact groups rejected", "", "[{\"objid\":42,\"name\":\"NewGroup\",\"tags\":\"\"},{\"objid\":43,\"name\":\"NewGroup\",\"tags\":\"\"}]", true);
await Check("Partial name ignored", "", "[{\"objid\":43,\"name\":\"NewGroup-other\",\"tags\":\"\"}]", true);
await Check("Malformed response diagnosed", "not JSON", "[]", true);
await Check("HTTP failure diagnosed", "", "[]", true, status: HttpStatusCode.BadRequest);
await Check("Malformed v1 response diagnosed", "", "", true);
await Check("Visibility beyond former five attempts", "", "[{\"objid\":42,\"name\":\"NewGroup\",\"tags\":\"\"}]", false, 42, emptyReads: 8);
await Check("Visibility timeout never repeats POST", "", "[]", true);
Console.WriteLine("13 regression tests passed. No live PRTG requests.");

static async Task Check(string test, string body, string groups, bool expectError, int expectedId = 0,
    HttpStatusCode status = HttpStatusCode.Created, int emptyReads = 0)
{
    int posts = 0, reads = 0;
    using var v2 = new HttpClient(new FakeHandler(request =>
    {
        if (request.Method != HttpMethod.Post) throw new Exception("Unexpected mutation");
        posts++;
        return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    })) { BaseAddress = new Uri("https://fake.invalid") };
    using var v1 = new HttpClient(new FakeHandler(request =>
    {
        var query = request.RequestUri!.Query;
        if (!query.Contains("filter_parentid=7")) throw new Exception("Missing direct-parent filter");
        reads++;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"groups\":" + (reads <= emptyReads ? "[]" : groups) + "}") };
    })) { BaseAddress = new Uri("https://fake.invalid") };
    var service = new PrtgGroupSyncService(new PrtgConfig { ApiKey = "fake", ApiUrl = "https://fake.invalid", ApiUrl2 = "https://fake.invalid" }, v1, v2, creationLookupAttempts: 30, creationLookupDelay: TimeSpan.FromMilliseconds(1));
    var method = typeof(PrtgGroupSyncService).GetMethod("CreateGroupV2Async", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var task = (Task)method.Invoke(service, ["NewGroup", 7, null, CancellationToken.None])!;
    try
    {
        await task;
        if (expectError) throw new Exception(test + ": expected PrtgApiException");
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var id = (int)result.GetType().GetProperty("ObjId")!.GetValue(result)!;
        if (id != expectedId) throw new Exception(test + ": wrong ID");
    }
    catch (PrtgApiException) when (expectError) { }
    if (posts != 1) throw new Exception(test + ": POST repeated");
    if (emptyReads > 0 && reads <= emptyReads) throw new Exception(test + ": no read retry");
    Console.WriteLine("PASS " + test);
}
sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(handler(request));
}
