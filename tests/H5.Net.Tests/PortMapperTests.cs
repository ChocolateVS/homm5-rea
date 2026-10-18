using System.Net;
using H5.Net.Nat;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace H5.Net.Tests;

public class PortMapperTests
{
    /// <summary>Exercises the UPnP IGD description parsing and AddPortMapping SOAP call against a fake router.</summary>
    [Fact]
    public async Task UpnpAddPortMappingTalksToIgd()
    {
        string? soapAction = null, soapBody = null;
        var app = WebApplication.CreateSlimBuilder();
        app.WebHost.UseUrls("http://127.0.0.1:0");
        var web = app.Build();
        web.MapGet("/desc.xml", () => Results.Text("""
            <?xml version="1.0"?>
            <root xmlns="urn:schemas-upnp-org:device-1-0"><device><deviceList><device><deviceList><device>
              <serviceList><service>
                <serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType>
                <controlURL>/ctl/IPConn</controlURL>
              </service></serviceList>
            </device></deviceList></device></deviceList></device></root>
            """, "text/xml"));
        web.MapPost("/ctl/IPConn", async (HttpContext ctx) =>
        {
            soapAction = ctx.Request.Headers["SOAPAction"];
            soapBody = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
            return Results.Text("<ok/>", "text/xml");
        });
        await web.StartAsync();
        try
        {
            using var http = new HttpClient();
            var services = await PortMapper.ReadDeviceAsync(http, new Uri(new Uri(web.Urls.First()), "/desc.xml"), default);
            var (control, service) = Assert.Single(services);
            Assert.EndsWith("/ctl/IPConn", control.AbsolutePath);
            Assert.True(await PortMapper.AddPortMappingAsync(http, control, service, 27615, "UDP", IPAddress.Parse("192.168.1.20"), "H5Reborn test", default));
            Assert.Equal("\"urn:schemas-upnp-org:service:WANIPConnection:1#AddPortMapping\"", soapAction);
            Assert.Contains("<NewExternalPort>27615</NewExternalPort>", soapBody);
            Assert.Contains("<NewProtocol>UDP</NewProtocol>", soapBody);
            Assert.Contains("<NewInternalClient>192.168.1.20</NewInternalClient>", soapBody);
        }
        finally { await web.StopAsync(); }
    }
}
