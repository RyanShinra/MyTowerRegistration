// =============================================================================
// BLAZOR WEBASSEMBLY ENTRY POINT
//
// This looks similar to the API's Program.cs but runs in a completely different
// context: inside the browser's WebAssembly engine, not on a server.
//
// Key differences from the API's Program.cs:
//   - WebAssemblyHostBuilder (not WebApplicationBuilder)
//   - No middleware pipeline — this is a client, not a server
//   - No Kestrel, no ports, no HTTP listener of any kind
//   - The "host" is a browser tab; the "runtime" is the WASM engine
//   - Services registered here are available to every .razor component via @inject
//
// Execution order when someone opens the admin page:
//   1. Browser downloads index.html from the Blazor dev server (or CDN in prod)
//   2. index.html loads blazor.webassembly.js
//   3. That script downloads the .NET WASM runtime + app DLLs (~10MB, cached after)
//   4. THIS FILE runs inside the WASM engine inside the browser tab
//   5. App.razor mounts into <div id="app">, replacing the loading spinner
//   6. The Router inside App.razor scans all @page directives and takes over navigation
//
// Compare to a React app:
//   index.js → ReactDOM.render(<App />, document.getElementById('root'))
//   This file is that, plus the service registration that would otherwise be
//   scattered across React context providers.
// =============================================================================

using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MyTowerRegistration.Admin;

WebAssemblyHostBuilder builder = WebAssemblyHostBuilder.CreateDefault(args);

// Mount the root Razor component into the <div id="app"> element in index.html.
// App.razor owns the Router — it's the single component that spawns everything else.
// Compare to React: ReactDOM.createRoot(document.getElementById('app')).render(<App />)
builder.RootComponents.Add<App>("#app");

// HeadOutlet lets individual page components inject content into <head>:
//   title, meta tags, canonical URLs, etc.
// Use it via <PageTitle>My Page</PageTitle> anywhere in the component tree.
// Compare to React Helmet / Next.js <Head>.
builder.RootComponents.Add<HeadOutlet>("head::after");

// =============================================================================
// CONFIGURATION
//
// Blazor WASM loads configuration from wwwroot/appsettings.json (and
// wwwroot/appsettings.Development.json in dev mode) — these are downloaded
// as static files by the browser at startup.
//
// IMPORTANT: Everything in wwwroot/ is PUBLIC. The browser downloads it.
// Never put secrets, passwords, or API keys here.
// The API URL is safe (it's a public endpoint anyway).
// =============================================================================

// The GraphQL API endpoint. Read from configuration so it can differ between
// local dev (localhost) and production (https://io.mytower.dev).
string apiBaseUrl = builder.Configuration["ApiBaseUrl"] ?? "http://localhost:5026";

// =============================================================================
// SERVICE REGISTRATION
//
// In Blazor WASM, "scoped" services live for the browser tab's lifetime.
// There is no per-request scope here — a new tab is the equivalent of
// a new request in server-side apps. "Singleton" and "Scoped" behave
// identically in WASM for this reason.
//
// Services registered here are injected into components via:
//   @inject IMyService MyService
// or into C# classes via constructor injection (same as anywhere in .NET).
// =============================================================================

// Raw HttpClient aimed at the API — used for manual calls before StrawberryShake
// is wired up. Once SS codegen runs, this is replaced by the typed client:
//   builder.Services.AddMyTowerClient()  ← SS generates this extension method
//
// Note: this is NOT the default template HttpClient (which aimed at HostEnvironment
// .BaseAddress — the Blazor dev server itself). We point at the API instead.
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(apiBaseUrl) });

// TODO: When StrawberryShake codegen is working, replace the raw HttpClient above
// with the generated typed client. SS will read .graphqlrc.json and the *.graphql
// operation files in the GraphQL/ folder to generate MyTowerClient:
//
//   builder.Services
//       .AddMyTowerClient()
//       .ConfigureHttpClient(client => client.BaseAddress = new Uri(apiBaseUrl));

await builder.Build().RunAsync();
