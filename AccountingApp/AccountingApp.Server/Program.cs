using AccountingApp.Client.Shared.Samples;
using AccountingApp.Server;
using AccountingApp.Server.Services;
using AccountingApp.Server.Services.AI;
using AccountingApp.Server.Services.FileManagement;
using Codeer.LowCode.Bindings.ApexCharts;
using Codeer.LowCode.Blazor.Extras;
using Codeer.LowCode.Blazor.Json;
using Codeer.LowCode.Blazor.License;
using Codeer.LowCode.Blazor.SystemSettings;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.ResponseCompression;
using PdfSharp.Fonts;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

//load dll.
typeof(CodeBehindSample).ToString();
ApexChartsServerInitializer.Initialize();
ExtrasServerInitializer.Initialize();

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var builder = WebApplication.CreateBuilder(args);

GlobalFontSettings.FontResolver = new CustomFontResolver();

LicenseManager.DomainLicense = builder.Configuration.GetSection("DomainLicense").Get<string>() ?? string.Empty;
LicenseManager.IsAutoUpdate = builder.Configuration.GetSection("IsLicenseAutoUpdate").Get<bool>();
SystemConfig.Instance.UseHotReload = builder.Configuration.GetSection("UseHotReload").Get<bool>();
SystemConfig.Instance.CanScriptDebug = builder.Configuration.GetSection("CanScriptDebug").Get<bool>();
SystemConfig.Instance.DataSources = builder.Configuration.GetSection("DataSources").Get<DataSource[]>() ?? [];
SystemConfig.Instance.FileStorages = builder.Configuration.GetSection("FileStorages").Get<FileStorage[]>() ?? [];
SystemConfig.Instance.TemporaryFileTableInfo = builder.Configuration.GetSection("TemporaryFileTableInfo").Get<TemporaryFileTableInfo[]>() ?? [];
SystemConfig.Instance.DesignFileDirectory = builder.Configuration["DesignFileDirectory"] ?? string.Empty;
SystemConfig.Instance.FontFileDirectory = builder.Configuration["FontFileDirectory"] ?? string.Empty;
SystemConfig.Instance.MailSettings = builder.Configuration.GetSection("MailSettings").Get<MailSettings>() ?? new();
SystemConfig.Instance.AISettings = builder.Configuration.GetSection("AISettings").Get<AISettings>() ?? new();
SystemConfig.Instance.NtaInvoiceSettings = builder.Configuration.GetSection("NtaInvoiceSettings").Get<NtaInvoiceSettings>() ?? new();
SystemConfig.Instance.PasswordCheckUserTableInfo = builder.Configuration.GetSection("PasswordCheckUserTableInfo").Get<PasswordCheckUserTableInfo>() ?? new();
SystemConfig.Instance.DataSources.ToList().ForEach(e => e.ConnectionString = builder.Configuration.GetConnectionString(e.Name) ?? string.Empty);
SystemConfig.Instance.FileStorages.ToList().ForEach(e => e.ConnectionString = builder.Configuration.GetConnectionString(e.Name) ?? string.Empty);

// Cookie authentication.
builder.UseCookieAuthentication();

builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();

builder.Services.AddControllers()
      .AddJsonOptions(options =>
      {
          options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
          options.JsonSerializerOptions.Converters.AddJsonConverters();
      });

if (SystemConfig.Instance.UseHotReload)
{
    builder.Services.AddSignalR();
    builder.Services.AddHostedService<FileWatcherService>();
}

// Compress dynamic responses (e.g. the design data fetched on every reload).
// Brotli/Gzip are decoded by the browser's native network stack, so this adds
// no decompression cost to the WASM runtime.
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes
        .Concat(["application/octet-stream"]);
});

//Localize
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    var supportedCultures = new[]
    {
        new CultureInfo("ja-JP")
    };
    //set neutral as default
    options.DefaultRequestCulture = new RequestCulture(string.Empty);
    options.SupportedCultures = supportedCultures;
    options.SupportedUICultures = supportedCultures;

    options.RequestCultureProviders.Add(new CustomRequestCultureProvider(async context =>
    {
        //localization by the request header
        var userLanguages = context.Request.Headers["Accept-Language"].ToString();
        var firstLanguage = userLanguages.Split(',').FirstOrDefault();
        if (firstLanguage == "ja") firstLanguage = "ja-JP";

        return await Task.FromResult(new ProviderCultureResult(firstLanguage));
    }));
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<DataService>();

var app = builder.Build();

app.UseResponseCompression();
app.UseRequestLocalization();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.UseRouting();

if (SystemConfig.Instance.UseHotReload)
{
    app.MapHub<HotReloadHub>("/hot_reload_hub");
}

// Cookie authentication.
app.UseCookieAuthentication();

app.MapRazorPages();
app.MapControllers();
app.MapFallbackToFile("index.html");

// Exception handling.
app.UseExceptionHandlerSendToFront();

app.Run();
