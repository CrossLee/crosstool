#include <windows.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <tlhelp32.h>
#include <wrl/client.h>

#include <array>
#include <cstring>
#include <filesystem>
#include <iomanip>
#include <iostream>
#include <limits>
#include <memory>
#include <set>
#include <sstream>
#include <string>
#include <string_view>
#include <system_error>
#include <utility>
#include <vector>

namespace
{
using Microsoft::WRL::ComPtr;

constexpr CLSID CLSID_CrosioCopyPath = {
    0x6ea97827,
    0x5b42,
    0x4e82,
    {0x8a, 0xbc, 0x4f, 0xc2, 0x2d, 0x3b, 0xe1, 0x29}};

class TestFailure final
{
public:
    explicit TestFailure(std::wstring message) : _message(std::move(message)) {}

    const std::wstring& Message() const noexcept { return _message; }

private:
    std::wstring _message;
};

[[noreturn]] void ThrowHResult(std::wstring_view operation, HRESULT result)
{
    std::wostringstream message;
    message << operation << L" failed with HRESULT 0x"
            << std::hex << std::uppercase << std::setw(8) << std::setfill(L'0')
            << static_cast<unsigned long>(result);
    throw TestFailure(message.str());
}

[[noreturn]] void ThrowWin32(std::wstring_view operation, DWORD error)
{
    std::wostringstream message;
    message << operation << L" failed with Win32 error " << error;
    throw TestFailure(message.str());
}

void CheckHResult(HRESULT result, std::wstring_view operation)
{
    if (FAILED(result))
    {
        ThrowHResult(operation, result);
    }
}

class ComApartment final
{
public:
    ComApartment()
    {
        const HRESULT result = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
        CheckHResult(result, L"CoInitializeEx");
        _initialized = true;
    }

    ~ComApartment() noexcept
    {
        if (_initialized)
        {
            CoUninitialize();
        }
    }

    ComApartment(const ComApartment&) = delete;
    ComApartment& operator=(const ComApartment&) = delete;

private:
    bool _initialized = false;
};

class UniqueHandle final
{
public:
    explicit UniqueHandle(HANDLE value) noexcept : _value(value) {}

    ~UniqueHandle() noexcept
    {
        if (IsValid())
        {
            CloseHandle(_value);
        }
    }

    UniqueHandle(const UniqueHandle&) = delete;
    UniqueHandle& operator=(const UniqueHandle&) = delete;

    bool IsValid() const noexcept
    {
        return _value != nullptr && _value != INVALID_HANDLE_VALUE;
    }

    HANDLE Get() const noexcept { return _value; }

private:
    HANDLE _value;
};

class LoadedModule final
{
public:
    explicit LoadedModule(const std::wstring& path)
        : _module(LoadLibraryExW(
              path.c_str(),
              nullptr,
              LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32))
    {
        if (_module == nullptr)
        {
            ThrowWin32(L"LoadLibraryExW", GetLastError());
        }
    }

    ~LoadedModule() noexcept
    {
        if (_module != nullptr)
        {
            FreeLibrary(_module);
        }
    }

    LoadedModule(const LoadedModule&) = delete;
    LoadedModule& operator=(const LoadedModule&) = delete;

    FARPROC FindExport(const char* name) const
    {
        FARPROC address = GetProcAddress(_module, name);
        if (address == nullptr)
        {
            ThrowWin32(L"GetProcAddress", GetLastError());
        }
        return address;
    }

private:
    HMODULE _module = nullptr;
};

class TemporaryDirectory final
{
public:
    TemporaryDirectory()
    {
        std::array<wchar_t, MAX_PATH + 1> temporaryPath{};
        const DWORD temporaryPathLength = GetTempPathW(
            static_cast<DWORD>(temporaryPath.size()), temporaryPath.data());
        if (temporaryPathLength == 0 || temporaryPathLength >= temporaryPath.size())
        {
            ThrowWin32(L"GetTempPathW", GetLastError());
        }

        GUID identifier{};
        CheckHResult(CoCreateGuid(&identifier), L"CoCreateGuid");

        std::array<wchar_t, 40> identifierText{};
        if (StringFromGUID2(
                identifier,
                identifierText.data(),
                static_cast<int>(identifierText.size())) == 0)
        {
            throw TestFailure(L"StringFromGUID2 failed");
        }

        _path = std::filesystem::path(temporaryPath.data()) /
            (L"Crosio-Shell-Smoke-" + std::wstring(identifierText.data()) + L"-测试");
        if (CreateDirectoryW(_path.c_str(), nullptr) == FALSE)
        {
            ThrowWin32(L"CreateDirectoryW(test root)", GetLastError());
        }
    }

    ~TemporaryDirectory() noexcept
    {
        std::error_code ignored;
        std::filesystem::remove_all(_path, ignored);
    }

    TemporaryDirectory(const TemporaryDirectory&) = delete;
    TemporaryDirectory& operator=(const TemporaryDirectory&) = delete;

    std::filesystem::path CreateFile(const std::wstring& name) const
    {
        const std::filesystem::path path = _path / name;
        const UniqueHandle file(CreateFileW(
            path.c_str(),
            GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr,
            CREATE_NEW,
            FILE_ATTRIBUTE_NORMAL,
            nullptr));
        if (!file.IsValid())
        {
            ThrowWin32(L"CreateFileW(test file)", GetLastError());
        }
        return path;
    }

    std::filesystem::path CreateFolder(const std::wstring& name) const
    {
        const std::filesystem::path path = _path / name;
        if (CreateDirectoryW(path.c_str(), nullptr) == FALSE)
        {
            ThrowWin32(L"CreateDirectoryW(test folder)", GetLastError());
        }
        return path;
    }

private:
    std::filesystem::path _path;
};

std::wstring GetAbsoluteLongPath(const std::filesystem::path& input)
{
    const DWORD absoluteRequired = GetFullPathNameW(input.c_str(), 0, nullptr, nullptr);
    if (absoluteRequired == 0)
    {
        ThrowWin32(L"GetFullPathNameW(size)", GetLastError());
    }

    std::vector<wchar_t> absoluteBuffer(absoluteRequired);
    const DWORD absoluteLength = GetFullPathNameW(
        input.c_str(),
        static_cast<DWORD>(absoluteBuffer.size()),
        absoluteBuffer.data(),
        nullptr);
    if (absoluteLength == 0 || absoluteLength >= absoluteBuffer.size())
    {
        ThrowWin32(L"GetFullPathNameW", GetLastError());
    }

    const DWORD longRequired = GetLongPathNameW(absoluteBuffer.data(), nullptr, 0);
    if (longRequired == 0)
    {
        ThrowWin32(L"GetLongPathNameW(size)", GetLastError());
    }

    std::vector<wchar_t> longBuffer(longRequired);
    const DWORD longLength = GetLongPathNameW(
        absoluteBuffer.data(),
        longBuffer.data(),
        static_cast<DWORD>(longBuffer.size()));
    if (longLength == 0 || longLength >= longBuffer.size())
    {
        ThrowWin32(L"GetLongPathNameW", GetLastError());
    }

    std::wstring result(longBuffer.data(), longLength);
    if (!std::filesystem::path(result).is_absolute())
    {
        throw TestFailure(L"The selected test item path is not absolute: " + result);
    }
    return result;
}

struct PidlDeleter final
{
    void operator()(ITEMIDLIST* value) const noexcept
    {
        CoTaskMemFree(value);
    }
};

using UniquePidl = std::unique_ptr<ITEMIDLIST, PidlDeleter>;

ComPtr<IShellItemArray> CreateShellItemArray(const std::vector<std::wstring>& paths)
{
    if (paths.empty())
    {
        throw TestFailure(L"A shell selection cannot be empty");
    }
    if (paths.size() > (std::numeric_limits<UINT>::max)())
    {
        throw TestFailure(L"The shell selection is too large");
    }

    std::vector<UniquePidl> ownedPidls;
    std::vector<PCIDLIST_ABSOLUTE> pidls;
    ownedPidls.reserve(paths.size());
    pidls.reserve(paths.size());

    for (const std::wstring& path : paths)
    {
        PIDLIST_ABSOLUTE rawPidl = nullptr;
        CheckHResult(
            SHParseDisplayName(path.c_str(), nullptr, &rawPidl, 0, nullptr),
            L"SHParseDisplayName");
        ownedPidls.emplace_back(rawPidl);
        pidls.push_back(rawPidl);
    }

    ComPtr<IShellItemArray> selection;
    CheckHResult(
        SHCreateShellItemArrayFromIDLists(
            static_cast<UINT>(pidls.size()), pidls.data(), selection.GetAddressOf()),
        L"SHCreateShellItemArrayFromIDLists");
    return selection;
}

class ClipboardReadScope final
{
public:
    ClipboardReadScope()
    {
        DWORD lastError = ERROR_SUCCESS;
        for (int attempt = 0; attempt < 20; ++attempt)
        {
            if (OpenClipboard(nullptr) != FALSE)
            {
                _opened = true;
                return;
            }
            lastError = GetLastError();
            Sleep(15);
        }

        ThrowWin32(
            L"OpenClipboard(read)",
            lastError == ERROR_SUCCESS ? ERROR_GEN_FAILURE : lastError);
    }

    ~ClipboardReadScope() noexcept
    {
        if (_opened)
        {
            CloseClipboard();
        }
    }

    ClipboardReadScope(const ClipboardReadScope&) = delete;
    ClipboardReadScope& operator=(const ClipboardReadScope&) = delete;

private:
    bool _opened = false;
};

class GlobalLockScope final
{
public:
    explicit GlobalLockScope(HGLOBAL value) : _value(value), _data(GlobalLock(value))
    {
        if (_data == nullptr)
        {
            ThrowWin32(L"GlobalLock(clipboard text)", GetLastError());
        }
    }

    ~GlobalLockScope() noexcept
    {
        if (_data != nullptr)
        {
            GlobalUnlock(_value);
        }
    }

    GlobalLockScope(const GlobalLockScope&) = delete;
    GlobalLockScope& operator=(const GlobalLockScope&) = delete;

    const wchar_t* Text() const noexcept
    {
        return static_cast<const wchar_t*>(_data);
    }

private:
    HGLOBAL _value;
    void* _data;
};

std::wstring ReadUnicodeClipboardText()
{
    const ClipboardReadScope clipboard;
    if (IsClipboardFormatAvailable(CF_UNICODETEXT) == FALSE)
    {
        throw TestFailure(L"CF_UNICODETEXT is not available after IExplorerCommand::Invoke");
    }

    HGLOBAL data = static_cast<HGLOBAL>(GetClipboardData(CF_UNICODETEXT));
    if (data == nullptr)
    {
        ThrowWin32(L"GetClipboardData(CF_UNICODETEXT)", GetLastError());
    }

    const SIZE_T bytes = GlobalSize(data);
    if (bytes < sizeof(wchar_t) || bytes % sizeof(wchar_t) != 0)
    {
        throw TestFailure(L"CF_UNICODETEXT contains an invalid global-memory payload");
    }

    const GlobalLockScope locked(data);
    const wchar_t* const begin = locked.Text();
    const size_t capacity = bytes / sizeof(wchar_t);
    const wchar_t* terminator = begin;
    while (terminator < begin + capacity && *terminator != L'\0')
    {
        ++terminator;
    }
    if (terminator == begin + capacity)
    {
        throw TestFailure(L"CF_UNICODETEXT is not null-terminated");
    }

    return std::wstring(begin, terminator);
}

void VerifyCopyPathCase(
    IExplorerCommand* command,
    const std::vector<std::wstring>& paths,
    const std::wstring& expected,
    std::wstring_view caseName)
{
    const ComPtr<IShellItemArray> selection = CreateShellItemArray(paths);
    CheckHResult(command->Invoke(selection.Get(), nullptr), caseName);

    const std::wstring actual = ReadUnicodeClipboardText();
    if (actual != expected)
    {
        throw TestFailure(
            std::wstring(caseName) + L" copied unexpected text. Expected [" + expected +
            L"], actual [" + actual + L"]");
    }
}

std::set<DWORD> FindCrosioProcessIds()
{
    const UniqueHandle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0));
    if (!snapshot.IsValid())
    {
        ThrowWin32(L"CreateToolhelp32Snapshot", GetLastError());
    }

    PROCESSENTRY32W process{};
    process.dwSize = static_cast<DWORD>(sizeof(process));

    std::set<DWORD> identifiers;
    if (Process32FirstW(snapshot.Get(), &process) == FALSE)
    {
        const DWORD error = GetLastError();
        if (error != ERROR_NO_MORE_FILES)
        {
            ThrowWin32(L"Process32FirstW", error);
        }
        return identifiers;
    }

    do
    {
        if (_wcsicmp(process.szExeFile, L"Crosio.exe") == 0)
        {
            identifiers.insert(process.th32ProcessID);
        }
    } while (Process32NextW(snapshot.Get(), &process) != FALSE);

    const DWORD iterationError = GetLastError();
    if (iterationError != ERROR_NO_MORE_FILES)
    {
        ThrowWin32(L"Process32NextW", iterationError);
    }
    return identifiers;
}

void VerifyNoCrosioProcessWasStarted(const std::set<DWORD>& processIdsBefore)
{
    Sleep(250);
    const std::set<DWORD> processIdsAfter = FindCrosioProcessIds();
    for (const DWORD processId : processIdsAfter)
    {
        if (!processIdsBefore.contains(processId))
        {
            throw TestFailure(
                L"The shell command unexpectedly started Crosio.exe (PID " +
                std::to_wstring(processId) + L")");
        }
    }
}

using DllGetClassObjectFunction = HRESULT(STDAPICALLTYPE*)(REFCLSID, REFIID, LPVOID*);

DllGetClassObjectFunction ResolveDllGetClassObject(const LoadedModule& module)
{
    const FARPROC rawFunction = module.FindExport("DllGetClassObject");
    static_assert(sizeof(DllGetClassObjectFunction) == sizeof(FARPROC));

    DllGetClassObjectFunction function = nullptr;
    std::memcpy(&function, &rawFunction, sizeof(function));
    return function;
}

void RunSmokeTest(const std::wstring& extensionPath)
{
    const ComApartment apartment;
    const std::set<DWORD> processIdsBefore = FindCrosioProcessIds();

    const TemporaryDirectory temporaryDirectory;
    const std::wstring filePath = GetAbsoluteLongPath(
        temporaryDirectory.CreateFile(L"Unicode 文件 截图-äöü.png"));
    const std::wstring folderPath = GetAbsoluteLongPath(
        temporaryDirectory.CreateFolder(L"Unicode 文件夹-测试"));

    {
        const LoadedModule module(extensionPath);
        const DllGetClassObjectFunction dllGetClassObject = ResolveDllGetClassObject(module);

        ComPtr<IClassFactory> factory;
        CheckHResult(
            dllGetClassObject(
                CLSID_CrosioCopyPath,
                IID_PPV_ARGS(factory.GetAddressOf())),
            L"DllGetClassObject");

        ComPtr<IExplorerCommand> command;
        CheckHResult(
            factory->CreateInstance(
                nullptr,
                IID_PPV_ARGS(command.GetAddressOf())),
            L"IClassFactory::CreateInstance");

        VerifyCopyPathCase(command.Get(), {filePath}, filePath, L"single Unicode file");
        VerifyCopyPathCase(command.Get(), {folderPath}, folderPath, L"single Unicode folder");
        VerifyCopyPathCase(
            command.Get(),
            {filePath, folderPath},
            filePath + L"\r\n" + folderPath,
            L"multi-select file and folder");
    }

    VerifyNoCrosioProcessWasStarted(processIdsBefore);
}
} // namespace

int wmain(int argumentCount, wchar_t* arguments[])
{
    if (argumentCount != 2)
    {
        std::wcerr << L"Usage: Crosio.Windows.ShellExtension.SmokeTests.exe "
                      L"<Crosio.Windows.ShellExtension.dll>\n";
        return 2;
    }

    try
    {
        RunSmokeTest(GetAbsoluteLongPath(arguments[1]));
        std::wcout << L"Native shell-extension smoke tests passed: file, folder, multi-select, "
                      L"CF_UNICODETEXT, and no Crosio.exe launch.\n";
        return 0;
    }
    catch (const TestFailure& failure)
    {
        std::wcerr << L"Native shell-extension smoke test failed: "
                   << failure.Message() << L'\n';
        return 1;
    }
    catch (const std::exception& exception)
    {
        std::cerr << "Native shell-extension smoke test failed unexpectedly: "
                  << exception.what() << '\n';
        return 1;
    }
    catch (...)
    {
        std::wcerr << L"Native shell-extension smoke test failed with an unknown exception.\n";
        return 1;
    }
}
