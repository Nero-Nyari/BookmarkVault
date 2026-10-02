using System;
using BookmarkVault.Models;
using BookmarkVault.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BookmarkVault.ViewModels;

/// <summary>账号列表中的一行</summary>
public sealed partial class AccountRowViewModel : ObservableObject
{
    public AccountEntry Model { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordText))]
    private bool _revealed;

    public AccountRowViewModel(AccountEntry model) => Model = model;

    public string Domain => Model.Domain;
    public string SiteName => string.IsNullOrWhiteSpace(Model.SiteName) ? Model.Domain : Model.SiteName;
    public string Username => string.IsNullOrWhiteSpace(Model.Username) ? "（未填写账号）" : Model.Username;
    public string Note => Model.Note ?? string.Empty;
    public bool HasPassword => !string.IsNullOrEmpty(Model.PasswordCipher);
    public bool AutoPrompt => Model.AutoPrompt;

    public string PasswordText
    {
        get
        {
            if (!HasPassword) return "（未保存密码）";
            if (!Revealed) return "••••••••";
            return AccountVault.Decrypt(Model.PasswordCipher) ?? "（解密失败）";
        }
    }

    public string AvatarColor => IconCache.AvatarColor(Model.Domain);
    public string AvatarText => IconCache.AvatarText(Model.Domain);
    public string UpdatedText => $"更新于 {Model.UpdatedAt:yyyy-MM-dd HH:mm}";

    public void Refresh() => OnPropertyChanged(string.Empty);
}
