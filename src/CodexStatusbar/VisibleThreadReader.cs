using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace CodexStatusbar;

/// <summary>在现有 UIA 线程上读取 Codex 主文档的标题，并以唯一名称解析当前会话。</summary>
internal sealed class VisibleThreadReader(string databasePath)
{
    private const int ReadIntervalMs = 200;
    private const int DocumentGraceMs = 500;
    private const int LookupRetryMs = 1000;
    private const int LookupRefreshMs = 5000;
    private static readonly Condition DocumentCondition = new AndCondition(
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document),
        new PropertyCondition(AutomationElement.AutomationIdProperty, "RootWebArea"));

    private IntPtr _window;
    private AutomationElement? _document;
    private string? _name;
    private string? _threadId;
    private long _nextRead;
    private long _nextLookup;
    private long _lastDocumentRead;
    private long _lastLookupSuccess;

    public string? Read(IntPtr window)
    {
        if (window != _window)
        {
            _window = window;
            _document = null;
            _name = null;
            _threadId = null;
            _nextRead = 0;
        }

        var now = Environment.TickCount64;
        if (window == IntPtr.Zero || now < _nextRead)
        {
            return _threadId;
        }

        _nextRead = now + ReadIntervalMs;
        if (!TryReadDocumentName(window, out var name))
        {
            // Chromium 重建控件时短暂保留同一窗口的已确认路由，并降低重新发现频率。
            if (_threadId is not null && now - _lastDocumentRead < DocumentGraceMs)
            {
                return _threadId;
            }

            _nextRead = now + LookupRetryMs;
            _name = null;
            _threadId = null;
            return null;
        }

        _lastDocumentRead = now;
        if (!string.Equals(name, _name, StringComparison.Ordinal))
        {
            _name = name;
            _threadId = null;
            _nextLookup = now;
        }

        if (name is not null && now >= _nextLookup)
        {
            var succeeded = StateDatabase.TryReadVisibleThread(databasePath, name, out var threadId);
            if (succeeded)
            {
                _threadId = threadId;
                _lastLookupSuccess = now;
            }
            else if (now - _lastLookupSuccess >= LookupRefreshMs + LookupRetryMs)
            {
                _threadId = null;
            }

            // 新名称可能尚未落库。命中后低频复查，及时发现重名或改名。
            _nextLookup = now + (succeeded && threadId is not null ? LookupRefreshMs : LookupRetryMs);
        }

        return _threadId;
    }

    private bool TryReadDocumentName(IntPtr window, out string? name)
    {
        name = null;
        try
        {
            if (_document is null)
            {
                var root = AutomationElement.FromHandle(window);
                var documents = root?.FindAll(TreeScope.Descendants, DocumentCondition);
                if (documents is not null)
                {
                    _document = documents.Cast<AutomationElement>().FirstOrDefault(document =>
                        document.GetCurrentPropertyValue(ValuePattern.ValueProperty) is "app://-/index.html");
                }
            }

            if (_document is null)
            {
                return false;
            }

            var title = _document.Current.Name?.Trim();
            name = string.IsNullOrEmpty(title) || title is "Codex" or "ChatGPT" ? null : title;
            return true;
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException
            or COMException or ArgumentException)
        {
            _document = null;
            return false;
        }
    }
}
