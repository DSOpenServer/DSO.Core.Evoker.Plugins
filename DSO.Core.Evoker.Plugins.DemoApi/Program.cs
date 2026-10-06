using DSO.Core.Evoker.Api;
using DSO.Core.Evoker.Commands;
using DSO.Core.Evoker.Plugins.Api;
using DSO.Core.Evoker.Plugins.DemoApi.Services;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

// ---- Evoker uçlarý: api/evoker/* (genel hedefler) + api/plugins/*, api/plugin-files/* (plugin yönetimi) ----
builder.Services.AddControllers()
    .AddEvokerApi(o =>
    {
        // Ýsimle eriþim izin listesi - appsettings "Evoker:AllowTypesFrom" (varsayýlan boþ = kapalý)
        o.AllowTypesFrom = builder.Configuration.GetSection("Evoker:AllowTypesFrom").Get<List<string>>() ?? new();
    })
    .AddEvokerPluginsApi(o => builder.Configuration.GetSection("Evoker:Plugins").Bind(o));

// Demo: web arayüzü baþka bir adresten çaðýrabilsin (güvenlik demo için açýk - bkz. README)
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

#if SWAGGER
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c => c.SwaggerDoc("v1", new() { Title = "DSO Evoker Demo API", Version = "v1" }));
#endif

var app = builder.Build();

// ---- Plugin olmayan örnek hedefler (host'un kendi sýnýflarý) - sabit anahtarlarla, demo.http'deki istekler için ----
var catalog = app.Services.GetRequiredService<EvokerCatalog>();
catalog.Register(typeof(Sayac), EvokerLifetime.Singleton, name: "Sayaç (singleton)", key: DemoAnahtarlar.Sayac);
catalog.Register(typeof(Sepet), EvokerLifetime.Scoped, name: "Sepet (komut baþýna)", key: DemoAnahtarlar.Sepet);
catalog.Register(typeof(Hesap), EvokerLifetime.Transient, name: "Hesap makinesi (adým baþýna)", key: DemoAnahtarlar.Hesap);
catalog.Register(typeof(SunucuSaati), name: "Sunucu saati (static)", key: DemoAnahtarlar.SunucuSaati);

#if SWAGGER
app.UseSwagger();
app.UseSwaggerUI();
#endif
app.MapControllers();

// Kök adres: uçlarýn listesi (Swagger: /swagger)
app.MapGet("/", () => Results.Json(new
{
    name = "DSO Evoker Demo API",
    swagger = "/swagger",
    docs = "Tüm istek örnekleri: demo.http",
    endpoints = new[]
    {
        "GET    /api/plugin-files                         Plugins klasöründeki DLL'ler",
        "GET    /api/plugin-files/scan?path=…             DLL'i çalýþtýrmadan tara (tipler, özet, kayýt þablonu)",
        "GET    /api/plugins                              kayýtlar (durum + ayarlar + özet)",
        "GET    /api/plugins/system                       kök, worker, sayýlar",
        "POST   /api/plugins                              kaydet",
        "GET    /api/plugins/{key}?samples=true           detay + komut þablonlarý + deðerler",
        "PATCH  /api/plugins/{key}                        güncelle",
        "DELETE /api/plugins/{key}                        sil",
        "POST   /api/plugins/{key}/activate|deactivate|reload|stop",
        "PUT    /api/plugins/{key}/mode                   canlý mod deðiþimi",
        "POST   /api/plugins/{key}/execute                JSON komut",
        "GET    /api/evoker/targets                       tüm hedefler (plugin'ler dahil)",
        "GET    /api/evoker/targets/{key}?samples=true",
        "POST   /api/evoker/targets/{key}/execute",
        "GET    /api/evoker/types                         isimle eriþim (izin listesi)",
        "POST   /api/evoker/types/{typeName}/execute"
    }
}, EvokerApiJson.Options));

app.Run();