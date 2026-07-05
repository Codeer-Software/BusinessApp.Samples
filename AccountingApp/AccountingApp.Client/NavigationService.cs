using AccountingApp.Client.Shared.Services;
using Codeer.LowCode.Blazor.RequestInterfaces;
using Microsoft.AspNetCore.Components;

namespace AccountingApp.Client
{
    public class NavigationService : NavigationServiceBase
    {
        readonly HttpService _http;
        readonly NavigationManager _nav;
        readonly IAppInfoService _appInfo;

        public NavigationService(NavigationManager nav, HttpService http, IAppInfoService appInfo) : base(nav)
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
