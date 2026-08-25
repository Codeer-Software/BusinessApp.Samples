using Microsoft.AspNetCore.Diagnostics;
using System.Net;

namespace BusinessApp.Server.Services
{
    public static class ExceptionHandlerUtils
    {
        public static void UseExceptionHandlerSendToFront(this WebApplication app)
        {
            app.UseExceptionHandler(errorApp =>
            {
                errorApp.Run(async context =>
                {
                    var exceptionHandlerPathFeature =
                        context.Features.Get<IExceptionHandlerPathFeature>();
                    if (exceptionHandlerPathFeature == null) return;

                    var ex = exceptionHandlerPathFeature.Error;
                    context.Response.ContentType = "text/plain";
                    context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                    await context.Response.WriteAsync(ex.GetMessages());
                });
            });
        }

        static string GetMessages(this Exception? ex)
        {
            var list = new List<string>();
            while (ex != null)
            {
                list.Add(ex.Message);
                ex = ex.InnerException;
            }
            //利用者に見せる文言なので改行は LF に揃える (ADR-0021 §2)。
            //例外の中身は raw string literal の改行 (LF) で作られており、
            //ここだけ CRLF で繋ぐと 1 つの文言の中で改行コードが割れる。
            return string.Join("\n", list);
        }
    }
}
