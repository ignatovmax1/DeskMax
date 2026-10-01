using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DeskMax.Contracts;

namespace DeskMax.Windows;
public partial class MainWindow : Window
{
    private readonly UpdateService updates = new();
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenSource lifetime = new();
    private RegisterDeviceResponse? device;
    private RotatingCodeResponse? code;
    private Guid? outgoing;
    private bool busy;
    private DateTimeOffset nextPoll;
    private Uri server = new("https://anydesk.familyserver.su/");
    public MainWindow()
    {
        InitializeComponent();
        DeviceNameText.Text = Environment.MachineName;
        VersionText.Text = $"Версия {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}";
        timer.Tick += async (_, _) => await TickAsync();
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await RegisterAsync();
        if (lifetime.IsCancellationRequested) return;
        timer.Start();
        try
        {
            var update = await updates.CheckAsync(lifetime.Token);
            if (update is not null && !lifetime.IsCancellationRequested)
            {
                UpdateButton.Content = $"Обновить до {update.Version}";
                Log($"Доступна версия {update.Version} в стабильном канале. Нажмите кнопку обновления.");
            }
        }
        catch (Exception) { /* Manual check reports errors; startup remains usable offline. */ }
    }
    private void Window_Closed(object? sender, EventArgs e) { timer.Stop(); lifetime.Cancel(); http.Dispose(); lifetime.Dispose(); }
    private void Log(string text) { Status.Text = text; HistoryList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {text}"); if (HistoryList.Items.Count > 200) HistoryList.Items.RemoveAt(200); }
    private async Task<T> Post<T>(string path, object body)
    {
        using var response = await http.PostAsJsonAsync(new Uri(server, path), body, lifetime.Token);
        if (response.StatusCode == HttpStatusCode.NotFound && path.EndsWith("/status", StringComparison.Ordinal)) outgoing = null;
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized) { device = null; code = null; DeviceIdText.Text = RotatingCodeText.Text = "—"; SetOnline(false); }
            throw new InvalidOperationException(response.StatusCode switch { HttpStatusCode.Unauthorized => "Регистрация устарела. Подключитесь заново в настройках.", HttpStatusCode.NotFound => "Код или запрос не найден. Проверьте срок действия.", HttpStatusCode.TooManyRequests => "Слишком много запросов. Повторите через минуту.", HttpStatusCode.BadRequest => "Проверьте код: нельзя подключаться к своему устройству.", HttpStatusCode.Conflict => "Запрос уже завершён или истёк.", _ => $"Ошибка сервера: {(int)response.StatusCode}." });
        }
        return await response.Content.ReadFromJsonAsync<T>(lifetime.Token) ?? throw new InvalidOperationException("Сервер вернул пустой ответ.");
    }
    private void SetOnline(bool online) { OnlineText.Text = online ? "Сервер доступен" : "Не подключено"; CopyButton.IsEnabled = ConnectButton.IsEnabled = online && device is not null; }
    private async Task RegisterAsync()
    {
        if (busy) return;
        busy = true; SaveButton.IsEnabled = false; SetOnline(false); device = null; code = null; outgoing = null; RequestsList.ItemsSource = null; DeviceIdText.Text = RotatingCodeText.Text = "—";
        try { device = await Post<RegisterDeviceResponse>("api/devices", new RegisterDeviceRequest(Environment.MachineName, "Windows")); await RefreshCode(); SetOnline(true); Log("Устройство зарегистрировано. Можно отправлять и принимать запросы."); }
        catch (Exception ex) { ShowError(ex); }
        finally { busy = false; SaveButton.IsEnabled = true; }
    }
    private async Task RefreshCode() { if (device is null) return; code = await Post<RotatingCodeResponse>($"api/devices/{device.DeviceId}/code", new ApproveSessionRequest(device.DeviceSecret)); DeviceIdText.Text = Format(device.DeviceId); RotatingCodeText.Text = Format(code.Code); }
    private static string Format(string value) => value.Length == 6 ? value.Insert(3, " ") : value;
    private async Task TickAsync()
    {
        if (code is not null) { var left = code.ExpiresAt - DateTimeOffset.UtcNow; ExpiryText.Text = left > TimeSpan.Zero ? $"Обновится через {left:mm\\:ss}" : "Обновление кода…"; }
        if (busy || device is null || DateTimeOffset.UtcNow < nextPoll) return;
        busy = true; nextPoll = DateTimeOffset.UtcNow.AddSeconds(5);
        try
        {
            if (code is null || code.ExpiresAt <= DateTimeOffset.UtcNow) await RefreshCode();
            var incoming = await Post<IncomingSessionResponse[]>($"api/devices/{device!.DeviceId}/sessions/incoming", new ApproveSessionRequest(device.DeviceSecret));
            var selected = (RequestsList.SelectedItem as RequestItem)?.Value.SessionId;
            RequestsList.ItemsSource = incoming.Select(x => new RequestItem(x, $"{x.RequesterDeviceName} · {Format(x.RequesterDeviceId)}")).ToArray();
            if (selected is not null) RequestsList.SelectedItem = RequestsList.Items.Cast<RequestItem>().FirstOrDefault(x => x.Value.SessionId == selected);
            RequestsEmpty.Text = incoming.Length == 0 ? "Новых запросов нет" : $"Ожидают подтверждения: {incoming.Length}";
            if (outgoing is Guid id) { var result = await Post<SessionResponse>($"api/sessions/{id}/status", new SessionCredentialsRequest(device.DeviceId, device.DeviceSecret)); if (result.Status != "pending-owner-confirmation") { outgoing = null; Log(SessionText(result.Status)); } }
            SetOnline(true);
        }
        catch (Exception ex) { SetOnline(false); ShowError(ex); nextPoll = DateTimeOffset.UtcNow.AddSeconds(15); }
        finally { busy = false; }
    }
    private static string SessionText(string status) => status switch { "approved" => "Владелец разрешил запрос. Передача экрана пока не реализована.", "rejected" => "Владелец отклонил запрос.", "revoked" => "Доступ отозван.", "expired" => "Срок запроса истёк. Отправьте новый запрос.", _ => "Запрос отправлен. Ожидаем разрешения владельца." };
    private void ShowError(Exception ex) { if (lifetime.IsCancellationRequested) return; Log(ex is HttpRequestException or TaskCanceledException ? "Сервер недоступен. Проверьте его запуск и адрес в настройках." : ex.Message); }
    private void CopyId_Click(object sender, RoutedEventArgs e) { if (device is null) return; try { Clipboard.SetText(device.DeviceId); Log("ID скопирован."); } catch (System.Runtime.InteropServices.COMException) { Log("Буфер обмена занят. Попробуйте ещё раз."); } }
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        var value = ConnectionCode.Text.Trim();
        if (value.Length != 6 || value.Any(c => c < '0' || c > '9')) { Log("Введите ровно 6 цифр временного кода."); ConnectionCode.Focus(); return; }
        if (busy || device is null) return;
        busy = true; ConnectButton.IsEnabled = false;
        try { var result = await Post<SessionResponse>("api/sessions", new CreateSessionRequest(value, device.DeviceId, device.DeviceSecret)); outgoing = result.Status == "pending-owner-confirmation" ? result.SessionId : null; Log(SessionText(result.Status)); }
        catch (Exception ex) { ShowError(ex); }
        finally { busy = false; ConnectButton.IsEnabled = device is not null; }
    }
    private async void Respond_Click(object sender, RoutedEventArgs e)
    {
        if (RequestsList.SelectedItem is not RequestItem item) { Log("Выберите входящий запрос."); return; }
        if (busy || device is null) return; busy = true;
        try { var action = (string)((Button)sender).Tag; await Post<SessionResponse>($"api/sessions/{item.Value.SessionId}/{action}", new ApproveSessionRequest(device.DeviceSecret)); Log(action == "approve" ? "Запрос разрешён. Передача экрана пока не реализована." : "Запрос отклонён."); nextPoll = DateTimeOffset.MinValue; }
        catch (Exception ex) { ShowError(ex); }
        finally { busy = false; }
    }
    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        var page = (string)((Button)sender).Tag;
        HomePage.Visibility = page == "Home" ? Visibility.Visible : Visibility.Collapsed;
        RequestsPage.Visibility = page == "Requests" ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = page == "History" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = page switch { "Requests" => "Разрешение остаётся за вами.", "History" => "История действий", "Settings" => "Настройки подключения", _ => "Ваш рабочий стол. Рядом." };
    }
    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!Uri.TryCreate(ServerAddress.Text.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))) { Log("Укажите HTTPS-адрес сервера или HTTP-адрес localhost."); return; }
        if (busy) { Log("Дождитесь завершения текущего запроса."); return; }
        server = uri; await RegisterAsync();
    }
    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false; UpdateButton.Content = "Проверяю…";
        try { var update = await updates.CheckAsync(lifetime.Token); if (update is null) Log("Новых обновлений в стабильном канале нет."); else if (MessageBox.Show($"Доступна версия {update.Version}. Скачать и установить?", "DeskMax", MessageBoxButton.YesNo) == MessageBoxResult.Yes) { UpdateButton.Content = "Скачиваю…"; await updates.DownloadVerifyAndLaunchAsync(update, lifetime.Token); Application.Current.Shutdown(); } }
        catch (Exception ex) { ShowError(ex); }
        finally { UpdateButton.IsEnabled = true; UpdateButton.Content = "Проверить обновления"; }
    }
    private sealed record RequestItem(IncomingSessionResponse Value, string Label);
}
