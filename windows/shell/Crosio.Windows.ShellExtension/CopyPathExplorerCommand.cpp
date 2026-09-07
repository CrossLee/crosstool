#include <windows.h>
#include <shobjidl.h>

#include <atomic>
#include <cstring>
#include <cwchar>
#include <limits>
#include <new>
#include <string>
#include <vector>

namespace
{
// Keep this CLSID synchronized with PackageManifest.fragment.xml.
constexpr CLSID CLSID_CrosioCopyPath = {
    0x6ea97827,
    0x5b42,
    0x4e82,
    {0x8a, 0xbc, 0x4f, 0xc2, 0x2d, 0x3b, 0xe1, 0x29}};

std::atomic<long> g_objectCount{0};
std::atomic<long> g_serverLockCount{0};

class ClipboardOwnerWindow final
{
public:
    ClipboardOwnerWindow() noexcept
        : _window(CreateWindowExW(
              0, L"STATIC", L"Crosio clipboard owner", 0,
              0, 0, 0, 0, HWND_MESSAGE, nullptr, nullptr, nullptr))
    {
    }

    ~ClipboardOwnerWindow() noexcept
    {
        if (_window != nullptr)
        {
            DestroyWindow(_window);
        }
    }

    ClipboardOwnerWindow(const ClipboardOwnerWindow&) = delete;
    ClipboardOwnerWindow& operator=(const ClipboardOwnerWindow&) = delete;

    HWND Get() const noexcept { return _window; }

private:
    HWND _window;
};

HRESULT DuplicateString(const wchar_t* value, PWSTR* output) noexcept
{
    if (output == nullptr)
    {
        return E_POINTER;
    }

    *output = nullptr;
    const size_t characters = wcslen(value) + 1;
    if (characters > (std::numeric_limits<size_t>::max)() / sizeof(wchar_t))
    {
        return E_OUTOFMEMORY;
    }

    auto* duplicate = static_cast<PWSTR>(CoTaskMemAlloc(characters * sizeof(wchar_t)));
    if (duplicate == nullptr)
    {
        return E_OUTOFMEMORY;
    }

    memcpy(duplicate, value, characters * sizeof(wchar_t));
    *output = duplicate;
    return S_OK;
}

HRESULT CopyUnicodeTextToClipboard(const std::wstring& text) noexcept
{
    if (text.empty())
    {
        return E_INVALIDARG;
    }

    const size_t characters = text.size() + 1;
    if (characters > (std::numeric_limits<size_t>::max)() / sizeof(wchar_t))
    {
        return E_OUTOFMEMORY;
    }

    HGLOBAL memory = GlobalAlloc(GMEM_MOVEABLE, characters * sizeof(wchar_t));
    if (memory == nullptr)
    {
        return HRESULT_FROM_WIN32(GetLastError());
    }

    auto* destination = static_cast<wchar_t*>(GlobalLock(memory));
    if (destination == nullptr)
    {
        const HRESULT result = HRESULT_FROM_WIN32(GetLastError());
        GlobalFree(memory);
        return result;
    }

    memcpy(destination, text.c_str(), characters * sizeof(wchar_t));
    GlobalUnlock(memory);

    // EmptyClipboard requires a real owner HWND before SetClipboardData.
    // A message-only window stays invisible and never opens the Crosio UI.
    const ClipboardOwnerWindow clipboardOwner;
    if (clipboardOwner.Get() == nullptr)
    {
        const DWORD error = GetLastError();
        GlobalFree(memory);
        return error == ERROR_SUCCESS ? E_FAIL : HRESULT_FROM_WIN32(error);
    }

    BOOL opened = FALSE;
    DWORD clipboardError = ERROR_SUCCESS;
    for (int attempt = 0; attempt < 8 && opened == FALSE; ++attempt)
    {
        opened = OpenClipboard(clipboardOwner.Get());
        if (opened == FALSE)
        {
            clipboardError = GetLastError();
            Sleep(15);
        }
    }

    if (opened == FALSE)
    {
        GlobalFree(memory);
        return clipboardError == ERROR_SUCCESS ? E_FAIL : HRESULT_FROM_WIN32(clipboardError);
    }

    HRESULT result = S_OK;
    do
    {
        if (EmptyClipboard() == FALSE)
        {
            result = HRESULT_FROM_WIN32(GetLastError());
            break;
        }

        if (SetClipboardData(CF_UNICODETEXT, memory) == nullptr)
        {
            result = HRESULT_FROM_WIN32(GetLastError());
            break;
        }

        // Windows owns this allocation after SetClipboardData succeeds.
        memory = nullptr;
    } while (false);

    if (memory != nullptr)
    {
        GlobalFree(memory);
    }
    CloseClipboard();
    return result;
}

class CopyPathExplorerCommand final : public IExplorerCommand
{
public:
    CopyPathExplorerCommand() noexcept
    {
        ++g_objectCount;
    }

    ~CopyPathExplorerCommand()
    {
        --g_objectCount;
    }

    IFACEMETHODIMP QueryInterface(REFIID interfaceId, void** object) noexcept override
    {
        if (object == nullptr)
        {
            return E_POINTER;
        }

        *object = nullptr;
        if (interfaceId == IID_IUnknown || interfaceId == IID_IExplorerCommand)
        {
            *object = static_cast<IExplorerCommand*>(this);
            AddRef();
            return S_OK;
        }

        return E_NOINTERFACE;
    }

    IFACEMETHODIMP_(ULONG) AddRef() noexcept override
    {
        return static_cast<ULONG>(InterlockedIncrement(&_referenceCount));
    }

    IFACEMETHODIMP_(ULONG) Release() noexcept override
    {
        const ULONG remaining = static_cast<ULONG>(InterlockedDecrement(&_referenceCount));
        if (remaining == 0)
        {
            delete this;
        }
        return remaining;
    }

    IFACEMETHODIMP GetTitle(IShellItemArray*, PWSTR* title) noexcept override
    {
        return DuplicateString(L"复制路径", title);
    }

    IFACEMETHODIMP GetIcon(IShellItemArray*, PWSTR* icon) noexcept override
    {
        if (icon == nullptr)
        {
            return E_POINTER;
        }
        *icon = nullptr;
        return S_FALSE;
    }

    IFACEMETHODIMP GetToolTip(IShellItemArray*, PWSTR* tooltip) noexcept override
    {
        return DuplicateString(L"复制所选文件或文件夹的完整路径", tooltip);
    }

    IFACEMETHODIMP GetCanonicalName(GUID* commandName) noexcept override
    {
        if (commandName == nullptr)
        {
            return E_POINTER;
        }
        *commandName = CLSID_CrosioCopyPath;
        return S_OK;
    }

    IFACEMETHODIMP GetState(
        IShellItemArray* selection,
        BOOL,
        EXPCMDSTATE* state) noexcept override
    {
        if (state == nullptr)
        {
            return E_POINTER;
        }

        DWORD count = 0;
        if (selection == nullptr || FAILED(selection->GetCount(&count)) || count == 0)
        {
            *state = ECS_HIDDEN;
            return S_OK;
        }

        *state = ECS_ENABLED;
        return S_OK;
    }

    IFACEMETHODIMP Invoke(IShellItemArray* selection, IBindCtx*) noexcept override
    {
        if (selection == nullptr)
        {
            return E_INVALIDARG;
        }

        DWORD count = 0;
        HRESULT result = selection->GetCount(&count);
        if (FAILED(result) || count == 0)
        {
            return FAILED(result) ? result : E_INVALIDARG;
        }

        std::vector<std::wstring> paths;
        try
        {
            paths.reserve(count);
        }
        catch (...)
        {
            return E_OUTOFMEMORY;
        }

        for (DWORD index = 0; index < count; ++index)
        {
            IShellItem* item = nullptr;
            result = selection->GetItemAt(index, &item);
            if (FAILED(result))
            {
                return result;
            }

            PWSTR path = nullptr;
            result = item->GetDisplayName(SIGDN_FILESYSPATH, &path);
            item->Release();
            if (FAILED(result))
            {
                return result;
            }

            try
            {
                paths.emplace_back(path);
            }
            catch (...)
            {
                CoTaskMemFree(path);
                return E_OUTOFMEMORY;
            }
            CoTaskMemFree(path);
        }

        std::wstring combined;
        try
        {
            size_t required = 1;
            for (const auto& path : paths)
            {
                if (path.size() > (std::numeric_limits<size_t>::max)() - required - 2)
                {
                    return E_OUTOFMEMORY;
                }
                required += path.size() + 2;
            }
            combined.reserve(required);

            for (size_t index = 0; index < paths.size(); ++index)
            {
                if (index != 0)
                {
                    combined.append(L"\r\n");
                }
                combined.append(paths[index]);
            }
        }
        catch (...)
        {
            return E_OUTOFMEMORY;
        }

        // Copy directly inside the Explorer command. Crosio.exe is not launched,
        // so this action can never create or activate the main window.
        return CopyUnicodeTextToClipboard(combined);
    }

    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) noexcept override
    {
        if (flags == nullptr)
        {
            return E_POINTER;
        }
        *flags = ECF_DEFAULT;
        return S_OK;
    }

    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** commands) noexcept override
    {
        if (commands == nullptr)
        {
            return E_POINTER;
        }
        *commands = nullptr;
        return E_NOTIMPL;
    }

private:
    volatile long _referenceCount = 1;
};

class CommandClassFactory final : public IClassFactory
{
public:
    CommandClassFactory() noexcept
    {
        ++g_objectCount;
    }

    ~CommandClassFactory()
    {
        --g_objectCount;
    }

    IFACEMETHODIMP QueryInterface(REFIID interfaceId, void** object) noexcept override
    {
        if (object == nullptr)
        {
            return E_POINTER;
        }

        *object = nullptr;
        if (interfaceId == IID_IUnknown || interfaceId == IID_IClassFactory)
        {
            *object = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }

        return E_NOINTERFACE;
    }

    IFACEMETHODIMP_(ULONG) AddRef() noexcept override
    {
        return static_cast<ULONG>(InterlockedIncrement(&_referenceCount));
    }

    IFACEMETHODIMP_(ULONG) Release() noexcept override
    {
        const ULONG remaining = static_cast<ULONG>(InterlockedDecrement(&_referenceCount));
        if (remaining == 0)
        {
            delete this;
        }
        return remaining;
    }

    IFACEMETHODIMP CreateInstance(
        IUnknown* outer,
        REFIID interfaceId,
        void** object) noexcept override
    {
        if (object == nullptr)
        {
            return E_POINTER;
        }
        *object = nullptr;
        if (outer != nullptr)
        {
            return CLASS_E_NOAGGREGATION;
        }

        auto* command = new (std::nothrow) CopyPathExplorerCommand();
        if (command == nullptr)
        {
            return E_OUTOFMEMORY;
        }

        const HRESULT result = command->QueryInterface(interfaceId, object);
        command->Release();
        return result;
    }

    IFACEMETHODIMP LockServer(BOOL lock) noexcept override
    {
        if (lock != FALSE)
        {
            ++g_serverLockCount;
        }
        else
        {
            --g_serverLockCount;
        }
        return S_OK;
    }

private:
    volatile long _referenceCount = 1;
};
} // namespace

extern "C" HRESULT __stdcall DllGetClassObject(
    REFCLSID classId,
    REFIID interfaceId,
    void** object)
{
    if (object == nullptr)
    {
        return E_POINTER;
    }
    *object = nullptr;

    if (!IsEqualCLSID(classId, CLSID_CrosioCopyPath))
    {
        return CLASS_E_CLASSNOTAVAILABLE;
    }

    auto* factory = new (std::nothrow) CommandClassFactory();
    if (factory == nullptr)
    {
        return E_OUTOFMEMORY;
    }

    const HRESULT result = factory->QueryInterface(interfaceId, object);
    factory->Release();
    return result;
}

extern "C" HRESULT __stdcall DllCanUnloadNow()
{
    return g_objectCount.load() == 0 && g_serverLockCount.load() == 0
        ? S_OK
        : S_FALSE;
}

BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, void*) noexcept
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}
