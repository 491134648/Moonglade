﻿#region Usings

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using AspNetCoreRateLimit;
using Edi.Captcha;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.ApplicationInsights.Extensibility.Implementation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Rewrite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moonglade.Auditing;
using Moonglade.Configuration;
using Moonglade.Configuration.Abstraction;
using Moonglade.Core;
using Moonglade.Core.Notification;
using Moonglade.Data;
using Moonglade.Data.Infrastructure;
using Moonglade.DataPorting;
using Moonglade.DateTimeOps;
using Moonglade.ImageStorage;
using Moonglade.Model;
using Moonglade.Model.Settings;
using Moonglade.OpmlFileWriter;
using Moonglade.Pingback;
using Moonglade.Setup;
using Moonglade.Web.Authentication;
using Moonglade.Web.Extensions;
using Moonglade.Web.Filters;
using Moonglade.Web.Middleware.DNT;
using Moonglade.Web.Middleware.FirstRun;
using Moonglade.Web.Middleware.PoweredBy;
using Moonglade.Web.Middleware.RobotsTxt;
using Moonglade.Web.SiteIconGenerator;
using Polly;

#endregion

namespace Moonglade.Web
{
    public class Startup
    {
        private ILogger<Startup> _logger;
        private readonly IConfigurationSection _appSettingsSection;
        private readonly IConfiguration _configuration;
        private readonly IWebHostEnvironment _environment;
        private readonly List<CultureInfo> _supportedCultures;

        public Startup(IConfiguration configuration, IWebHostEnvironment env)
        {
            _configuration = configuration;
            _environment = env;
            _appSettingsSection = _configuration.GetSection(nameof(AppSettings));

            _supportedCultures = new List<CultureInfo>
            {
                new CultureInfo("en-US"),
                new CultureInfo("zh-CN")
            };
        }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddOptions();
            services.AddMemoryCache();
            services.AddRateLimit(_configuration.GetSection("IpRateLimiting"));

            services.Configure<AppSettings>(_appSettingsSection);

            var authentication = new AuthenticationSettings();
            _configuration.Bind(nameof(Authentication), authentication);
            services.Configure<AuthenticationSettings>(_configuration.GetSection(nameof(Authentication)));

            var imageStorage = new ImageStorageSettings();
            _configuration.Bind(nameof(ImageStorage), imageStorage);
            services.Configure<ImageStorageSettings>(_configuration.GetSection(nameof(ImageStorage)));

            services.AddSession(options =>
            {
                options.IdleTimeout = TimeSpan.FromMinutes(20);
                options.Cookie.HttpOnly = true;
            });

            services.AddApplicationInsightsTelemetry();
            services.AddMoongladeAuthenticaton(authentication);

            services.AddLocalization(options => options.ResourcesPath = "Resources");
            services.AddMvc(options =>
                            options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()))
                    .AddViewLocalization()
                    .AddDataAnnotationsLocalization();

            services.Configure<RequestLocalizationOptions>(options =>
            {
                options.DefaultRequestCulture = new RequestCulture("en-US");
                options.SupportedCultures = _supportedCultures;
                options.SupportedUICultures = _supportedCultures;
            });

            services.AddAntiforgery(options =>
            {
                const string cookieBaseName = "CSRF-TOKEN-MOONGLADE";
                options.Cookie.Name = $"X-{cookieBaseName}";
                options.FormFieldName = $"{cookieBaseName}-FORM";
            });

            services.AddMoongladeImageStorage(imageStorage, _environment.ContentRootPath);
            services.AddScoped(typeof(IRepository<>), typeof(DbContextRepository<>));
            services.TryAddSingleton<IActionContextAccessor, ActionContextAccessor>();
            services.AddSingleton<IBlogConfig, BlogConfig>();
            services.AddScoped<IMoongladeAudit, MoongladeAudit>();
            services.AddScoped<DeleteSubscriptionCache>();
            services.AddScoped<ISiteIconGenerator, FileSystemSiteIconGenerator>();
            services.AddScoped<IDateTimeResolver>(c =>
                new DateTimeResolver(c.GetService<IBlogConfig>().GeneralSettings.TimeZoneUtcOffset));

            services.AddScoped<IExportManager, ExportManager>();
            services.AddScoped<IPingbackSender, PingbackSender>();
            services.AddScoped<IPingbackReceiver, PingbackReceiver>();
            services.AddScoped<IFileSystemOpmlWriter, FileSystemOpmlWriter>();
            services.AddScoped<IFileNameGenerator>(gen => new GuidFileNameGenerator(Guid.NewGuid()));
            services.AddSessionBasedCaptcha();

            var asm = Assembly.GetAssembly(typeof(MoongladeService));
            if (null != asm)
            {
                var types = asm.GetTypes().Where(t => t.IsClass && t.IsPublic && t.Name.EndsWith("Service"));
                foreach (var t in types)
                {
                    services.AddScoped(t, t);
                }
            }

            services.AddHttpClient<IMoongladeNotificationClient, NotificationClient>()
                    .AddTransientHttpErrorPolicy(builder =>
                            builder.WaitAndRetryAsync(3, retryCount =>
                            TimeSpan.FromSeconds(Math.Pow(2, retryCount)),
                                (result, span, retryCount, context) =>
                                {
                                    _logger?.LogWarning($"Request failed with {result.Result.StatusCode}. Waiting {span} before next retry. Retry attempt {retryCount}/3.");
                                }));

            services.AddDbContext<MoongladeDbContext>(options =>
                    options.UseLazyLoadingProxies()
                           .UseSqlServer(_configuration.GetConnectionString(Constants.DbConnectionName), sqlOptions =>
                               {
                                   sqlOptions.EnableRetryOnFailure(
                                       3,
                                       TimeSpan.FromSeconds(30),
                                       null);
                               }));
        }

        public void Configure(
            IApplicationBuilder app,
            ILogger<Startup> logger,
            IHostApplicationLifetime appLifetime,
            TelemetryConfiguration configuration)
        {
            _logger = logger;
            var enforceHttps = bool.Parse(_appSettingsSection["EnforceHttps"]);
            var allowExtScripts = bool.Parse(_appSettingsSection["AllowExternalScripts"]);

            // Support Chinese contents
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            if (!_environment.IsProduction())
            {
                _logger.LogWarning($"Running in environment: {_environment.EnvironmentName}. Application Insights disabled.");

                configuration.DisableTelemetry = true;
                TelemetryDebugWriter.IsTracingDisabled = true;
            }

            appLifetime.ApplicationStarted.Register(() =>
            {
                _logger.LogInformation("Moonglade started.");
            });
            appLifetime.ApplicationStopping.Register(() =>
            {
                _logger.LogInformation("Moonglade is stopping...");
            });
            appLifetime.ApplicationStopped.Register(() =>
            {
                _logger.LogInformation("Moonglade stopped.");
            });


            app.UseSecurityHeaders(new HeaderPolicyCollection()
                .AddFrameOptionsSameOrigin()
                .AddXssProtectionEnabled()
                .AddContentTypeOptionsNoSniff()
                .AddContentSecurityPolicy(csp =>
                {
                    if (enforceHttps)
                    {
                        csp.AddUpgradeInsecureRequests();
                    }
                    csp.AddFormAction()
                        .Self();

                    if (!allowExtScripts)
                    {
                        csp.AddScriptSrc()
                            .Self()
                            .UnsafeInline()
                            .UnsafeEval()
                            // Whitelist Azure Application Insights
                            .From("https://*.vo.msecnd.net")
                            .From("https://*.services.visualstudio.com");
                    }
                })
                // Microsoft believes privacy is a fundamental human right
                // So should I
                .AddFeaturePolicy(builder =>
                {
                    builder.AddCamera().None();
                    builder.AddMicrophone().None();
                    builder.AddPayment().None();
                    builder.AddUsb().None();
                })
                .RemoveServerHeader()
            );

            app.UseRobotsTxt();

            TryUseUrlRewrite(app);
            app.UseMiddleware<PoweredByMiddleware>();
            app.UseMiddleware<DNTMiddleware>();
            app.UseMiddleware<FirstRunMiddleware>();

            if (_environment.IsDevelopment())
            {
                app.UseRouteDebugger();
                _logger.LogWarning($"Running in environment: {_environment.EnvironmentName}. Detailed error page enabled.");
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/error");
                app.UseStatusCodePagesWithReExecute("/error", "?statusCode={0}");
            }

            if (enforceHttps)
            {
                _logger.LogInformation("HTTPS is enforced.");
                app.UseHttpsRedirection();
                app.UseHsts();
            }

            app.UseRequestLocalization(new RequestLocalizationOptions
            {
                DefaultRequestCulture = new RequestCulture("en-US"),
                SupportedCultures = _supportedCultures,
                SupportedUICultures = _supportedCultures
            });

            app.UseStaticFiles();
            app.UseSession();

            app.UseIpRateLimiting();
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGet("/ping", async context =>
                {
                    context.Response.Headers.Add("X-Moonglade-Version", Utils.AppVersion);
                    await context.Response.WriteAsync(
                        $"Moonglade Version: {Utils.AppVersion}, .NET Core {Environment.Version}", Encoding.UTF8);
                });
                endpoints.MapControllerRoute(
                    "default",
                    "{controller=Home}/{action=Index}/{id?}");
                endpoints.MapRazorPages();
            });
        }

        #region Private Helpers

        private void TryUseUrlRewrite(IApplicationBuilder app)
        {
            try
            {
                var options = new RewriteOptions()
                    .AddRedirect("(.*)/$", "$1")
                    .AddRedirect("(index|default).(aspx?|htm|s?html|php|pl|jsp|cfm)", "/");

                app.UseRewriter(options);
            }
            catch (Exception e)
            {
                _logger.LogError(e, nameof(TryUseUrlRewrite));
            }
        }

        #endregion
    }
}