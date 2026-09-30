using System.Windows;
namespace DeskMax.Windows;
public partial class MainWindow : Window
{
    private readonly UpdateService updates = new();
    public MainWindow() => InitializeComponent();
    private void CopyId_Click(object sender, RoutedEventArgs e) => Clipboard.SetText(DeviceIdText.Text.Replace(" ", ""));
    private void Connect_Click(object sender, RoutedEventArgs e) => Status.Text = ConnectionCode.Text.Count(char.IsDigit) == 6 ? "Запрос отправлен — ожидается подтверждение владельца" : "Введите ровно 6 цифр";
    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false; UpdateButton.Content = "Проверяю…";
        try { var update = await updates.CheckAsync(); if (update is null) MessageBox.Show("Установлена последняя версия.", "DeskMax"); else if (MessageBox.Show($"Доступна версия {update.Version}. Скачать и установить?", "DeskMax", MessageBoxButton.YesNo) == MessageBoxResult.Yes) { UpdateButton.Content = "Скачиваю…"; await updates.DownloadVerifyAndLaunchAsync(update); Application.Current.Shutdown(); } }
        catch (Exception ex) { MessageBox.Show($"Не удалось проверить обновления: {ex.Message}", "DeskMax", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { UpdateButton.IsEnabled = true; UpdateButton.Content = "Проверить обновления"; }
    }
}
