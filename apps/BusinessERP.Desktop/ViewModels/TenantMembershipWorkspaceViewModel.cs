using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using BusinessERP.Desktop.Services;

namespace BusinessERP.Desktop.ViewModels;

public sealed class TenantMembershipWorkspaceViewModel : INotifyPropertyChanged
{
    private ErpApiClient? _apiClient;
    private TenantMemberApiDto? _selectedMember;
    private string _userIdText = "";
    private string _assignRoleCode = "VIEWER";
    private string _selectedRoleCode = "VIEWER";
    private string _selectedStatus = "ACTIVE";
    private string _statusMessage = "سجّل الدخول وفعّل المستأجر لإدارة العضويات.";
    private bool _isBusy;

    public ObservableCollection<TenantMemberApiDto> Members { get; } = [];

    public IReadOnlyList<string> RoleCodes { get; } = ["OWNER", "ADMIN", "ACCOUNTANT", "VIEWER"];
    public IReadOnlyList<string> StatusCodes { get; } = ["ACTIVE", "SUSPENDED", "REMOVED"];

    public event PropertyChangedEventHandler? PropertyChanged;

    public TenantMemberApiDto? SelectedMember
    {
        get => _selectedMember;
        set
        {
            if (ReferenceEquals(_selectedMember, value))
                return;

            _selectedMember = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedMemberSummary));
            if (value is not null)
            {
                SelectedRoleCode = value.RoleCode;
                SelectedStatus = value.Status;
            }
        }
    }

    public string UserIdText
    {
        get => _userIdText;
        set { if (_userIdText == value) return; _userIdText = value; OnPropertyChanged(); }
    }

    public string AssignRoleCode
    {
        get => _assignRoleCode;
        set { if (_assignRoleCode == value) return; _assignRoleCode = value; OnPropertyChanged(); }
    }

    public string SelectedRoleCode
    {
        get => _selectedRoleCode;
        set { if (_selectedRoleCode == value) return; _selectedRoleCode = value; OnPropertyChanged(); }
    }

    public string SelectedStatus
    {
        get => _selectedStatus;
        set { if (_selectedStatus == value) return; _selectedStatus = value; OnPropertyChanged(); }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (_statusMessage == value)
                return;
            _statusMessage = value;
            OnPropertyChanged();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value)
                return;
            _isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanEdit));
        }
    }

    public bool CanEdit => !IsBusy && _apiClient is not null;
    public string WorkspaceContextSummary =>
        SelectedMember is null
            ? $"إدارة عضويات المستأجر الحالي · {Members.Count} عضوية معروضة"
            : $"عضوية: {SelectedMember.DisplayName} · الدور {SelectedMember.RoleCode} · الحالة {SelectedMember.Status}";
    public string SelectedMemberSummary =>
        SelectedMember is null
            ? "اختر عضوًا من الجدول لتعديل دوره أو حالته."
            : $"{SelectedMember.DisplayName} · {SelectedMember.Email} · {SelectedMember.UserId:D}";

    public async Task SetApiClientAsync(ErpApiClient? apiClient)
    {
        _apiClient = apiClient;
        OnPropertyChanged(nameof(CanEdit));
        if (apiClient is null)
        {
            Members.Clear();
            SelectedMember = null;
            StatusMessage = "سجّل الدخول وفعّل المستأجر لإدارة العضويات.";
            OnPropertyChanged(nameof(WorkspaceContextSummary));
            return;
        }

        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_apiClient is null)
        {
            StatusMessage = "لم يتم تفعيل المستأجر بعد.";
            return;
        }

        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            var previousId = SelectedMember?.UserId;
            var rows = await _apiClient.GetTenantMembersAsync();
            Members.Clear();
            foreach (var row in rows)
                Members.Add(row);

            SelectedMember = previousId is Guid id
                ? Members.FirstOrDefault(x => x.UserId == id)
                : null;

            StatusMessage = $"تم تحميل {Members.Count} عضوية. إدارة المستخدمين هنا تخص الهويات المهيأة مسبقًا فقط.";
        }
        catch (ErpApiException ex)
        {
            StatusMessage = ex.Message;
        }
        catch (HttpRequestException)
        {
            StatusMessage = "تعذر الاتصال بالخادم. تحقق من الشبكة وعنوان API.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"تعذر تحميل العضويات: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(WorkspaceContextSummary));
        }
    }

    public async Task AssignMemberAsync()
    {
        if (_apiClient is null || IsBusy)
            return;

        if (!Guid.TryParse(UserIdText.Trim(), out var userId) || userId == Guid.Empty)
        {
            StatusMessage = "أدخل GUID صحيحًا لمستخدم موجود ومجهز في موفر الهوية.";
            return;
        }

        if (!RoleCodes.Contains(AssignRoleCode, StringComparer.Ordinal))
        {
            StatusMessage = "اختر دورًا صالحًا للمستخدم.";
            return;
        }

        IsBusy = true;
        try
        {
            var added = await _apiClient.AssignTenantMemberAsync(
                new AssignTenantMemberApiRequest(userId, AssignRoleCode));
            UserIdText = "";
            await RefreshAfterMutationAsync(added.UserId);
            StatusMessage = $"تم إسناد {added.DisplayName} بدور {added.RoleCode}.";
        }
        catch (ErpApiException ex)
        {
            StatusMessage = ex.Message;
        }
        catch (HttpRequestException)
        {
            StatusMessage = "تعذر الاتصال بالخادم. تحقق من الشبكة وعنوان API.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"تعذر إسناد العضوية: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(WorkspaceContextSummary));
        }
    }

    public async Task UpdateMemberAsync()
    {
        if (_apiClient is null || IsBusy)
            return;

        if (SelectedMember is null)
        {
            StatusMessage = "اختر عضوًا من الجدول أولًا.";
            return;
        }

        if (!RoleCodes.Contains(SelectedRoleCode, StringComparer.Ordinal) ||
            !StatusCodes.Contains(SelectedStatus, StringComparer.Ordinal))
        {
            StatusMessage = "اختر دورًا وحالة صالحين.";
            return;
        }

        var memberId = SelectedMember.UserId;
        IsBusy = true;
        try
        {
            var updated = await _apiClient.UpdateTenantMemberAsync(
                memberId, new UpdateTenantMemberApiRequest(SelectedRoleCode, SelectedStatus));
            await RefreshAfterMutationAsync(updated.UserId);
            StatusMessage = $"تم تحديث عضوية {updated.DisplayName}: {updated.RoleCode} / {updated.Status}.";
        }
        catch (ErpApiException ex)
        {
            StatusMessage = ex.Message;
        }
        catch (HttpRequestException)
        {
            StatusMessage = "تعذر الاتصال بالخادم. تحقق من الشبكة وعنوان API.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"تعذر تحديث العضوية: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(WorkspaceContextSummary));
        }
    }

    private async Task RefreshAfterMutationAsync(Guid selectUserId)
    {
        var rows = await _apiClient!.GetTenantMembersAsync();
        Members.Clear();
        foreach (var row in rows)
            Members.Add(row);
        SelectedMember = Members.FirstOrDefault(x => x.UserId == selectUserId);
        OnPropertyChanged(nameof(WorkspaceContextSummary));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
