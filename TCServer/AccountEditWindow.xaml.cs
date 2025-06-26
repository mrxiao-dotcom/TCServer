using System;
using System.Windows;
using TCServer.Common.Models;

namespace TCServer
{
    public partial class AccountEditWindow : Window
    {
        public AccountInfo? AccountInfo { get; private set; }
        private readonly bool _isEditMode;

        public AccountEditWindow()
        {
            InitializeComponent();
            _isEditMode = false;
            Title = "添加账户";
        }

        public AccountEditWindow(AccountInfo accountInfo) : this()
        {
            _isEditMode = true;
            Title = "编辑账户";
            
            // 加载现有账户信息
            LoadAccountInfo(accountInfo);
        }

        private void LoadAccountInfo(AccountInfo accountInfo)
        {
            txtAcctName.Text = accountInfo.AcctName ?? "";
            txtMemo.Text = accountInfo.Memo ?? "";
            txtApiKey.Text = accountInfo.ApiKey ?? "";
            txtApiSecret.Password = accountInfo.SecretKey ?? "";
            txtEmail.Text = accountInfo.Email ?? "";
            
            // 保存原始账户信息
            AccountInfo = new AccountInfo
            {
                AcctId = accountInfo.AcctId,
                AcctName = accountInfo.AcctName,
                AcctDate = accountInfo.AcctDate,
                Memo = accountInfo.Memo,
                ApiKey = accountInfo.ApiKey,
                SecretKey = accountInfo.SecretKey,
                ApiPass = accountInfo.ApiPass,
                State = accountInfo.State,
                Status = accountInfo.Status,
                Email = accountInfo.Email,
                GroupId = accountInfo.GroupId,
                SendFlag = accountInfo.SendFlag
            };
        }

        private void btnOK_Click(object sender, RoutedEventArgs e)
        {
            if (ValidateInput())
            {
                SaveAccountInfo();
                DialogResult = true;
                Close();
            }
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private bool ValidateInput()
        {
            // 验证账户名
            if (string.IsNullOrWhiteSpace(txtAcctName.Text))
            {
                MessageBox.Show("请输入账户名", "验证失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtAcctName.Focus();
                return false;
            }

            // 验证API Key
            if (string.IsNullOrWhiteSpace(txtApiKey.Text))
            {
                MessageBox.Show("请输入API Key", "验证失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtApiKey.Focus();
                return false;
            }

            // 验证API Secret
            if (string.IsNullOrWhiteSpace(txtApiSecret.Password))
            {
                MessageBox.Show("请输入API Secret", "验证失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtApiSecret.Focus();
                return false;
            }

            // 验证API Key长度（币安API Key通常是64位）
            if (txtApiKey.Text.Length < 32)
            {
                MessageBox.Show("API Key长度不正确，请检查", "验证失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtApiKey.Focus();
                return false;
            }

            // 验证API Secret长度
            if (txtApiSecret.Password.Length < 32)
            {
                MessageBox.Show("API Secret长度不正确，请检查", "验证失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtApiSecret.Focus();
                return false;
            }

            // 验证邮箱格式（如果填写了的话）
            if (!string.IsNullOrWhiteSpace(txtEmail.Text))
            {
                try
                {
                    var addr = new System.Net.Mail.MailAddress(txtEmail.Text);
                    if (addr.Address != txtEmail.Text)
                    {
                        throw new FormatException();
                    }
                }
                catch
                {
                    MessageBox.Show("邮箱格式不正确", "验证失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    txtEmail.Focus();
                    return false;
                }
            }

            return true;
        }

        private void SaveAccountInfo()
        {
            if (AccountInfo == null)
            {
                AccountInfo = new AccountInfo();
            }

            AccountInfo.AcctName = txtAcctName.Text.Trim();
            AccountInfo.Memo = txtMemo.Text.Trim();
            AccountInfo.ApiKey = txtApiKey.Text.Trim();
            AccountInfo.SecretKey = txtApiSecret.Password;
            AccountInfo.Email = string.IsNullOrWhiteSpace(txtEmail.Text) ? null : txtEmail.Text.Trim();

            // 如果是新增模式，设置默认值
            if (!_isEditMode)
            {
                AccountInfo.AcctDate = DateTime.Now;
                AccountInfo.State = 1;
                AccountInfo.Status = 1;
                AccountInfo.GroupId = 4; // 币安交易所
                AccountInfo.SendFlag = 0;
            }
        }
    }
} 