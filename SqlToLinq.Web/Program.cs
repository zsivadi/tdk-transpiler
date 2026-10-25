using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<SqlToLinq.Web.App>("#app");

await builder.Build().RunAsync();
