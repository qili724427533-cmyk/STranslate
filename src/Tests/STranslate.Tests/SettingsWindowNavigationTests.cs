using STranslate.Plugin;
using STranslate.ViewModels.Pages;
using STranslate.Views;

namespace STranslate.Tests;

/// <summary>
/// 验证设置窗口把服务选中状态应用到当前导航页面的 ViewModel。
/// </summary>
public class SettingsWindowNavigationTests
{
    /// <summary>
    /// 验证导航携带的服务会成为页面的当前选中项。
    /// </summary>
    [Fact]
    public void ApplySelectedServiceSetsSelectionOnNavigatedPageViewModel()
    {
        var service = new Service();
        var viewModel = new TestServiceSelectionViewModel();

        SettingsWindow.ApplySelectedService(viewModel, service);

        Assert.Same(service, viewModel.SelectedItem);
    }

    /// <summary>
    /// 验证普通导航不会清除页面已有的服务选中状态。
    /// </summary>
    [Fact]
    public void ApplySelectedServiceDoesNotClearExistingSelectionWhenNoServiceWasProvided()
    {
        var service = new Service();
        var viewModel = new TestServiceSelectionViewModel { SelectedItem = service };

        SettingsWindow.ApplySelectedService(viewModel, null);

        Assert.Same(service, viewModel.SelectedItem);
    }

    private sealed class TestServiceSelectionViewModel : IServiceSelectionViewModel
    {
        public Service? SelectedItem { get; set; }
    }
}
