using BusinessApp.Client.Shared.Services;
using Codeer.LowCode.Blazor.Extras.Services;
using Codeer.LowCode.Blazor.RequestInterfaces;
using Microsoft.AspNetCore.Components;

namespace BusinessApp.Client
{
    public class NavigationService : NavigationServiceBase
    {
        readonly IHttpService _http;
        readonly NavigationManager _nav;
        readonly IAppInfoService _appInfo;

        public NavigationService(NavigationManager nav, IHttpService http, IAppInfoService appInfo) : base(nav)
        {
            _http = http;
            _nav = nav;
            _appInfo = appInfo;
        }

        public override bool CanLogout => true;

        public override async Task Logout()
        {
            await _http.PostAsJsonAsync("api/account/logout", "");
            _nav.NavigateTo("/", true);
        }
    }
}
