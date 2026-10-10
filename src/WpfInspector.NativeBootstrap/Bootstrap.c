/*
 * Bootstrap.c
 * 対象の .NET 8 以降のプロセスに LoadLibraryW で注入されるネイティブ DLL。
 * hostfxr.dll (既にプロセス内に存在) の API を使って
 * WpfInspector.InspectionPayload.dll をデフォルト AssemblyLoadContext に読み込む。
 */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <strsafe.h>
#include <stdio.h>
#include <string.h>

/* ---- hostfxr 戻り値定数 -------------------------------------------------- */

/*
 * hostfxr_initialize_for_runtime_config の正常リターンコード一覧:
 *   0           : Success
 *   1           : Success_HostAlreadyInitialized (同プロセス内で既初期化)
 *   0x80008060  : HostApiAlreadyInitialized      (int32_t では負値)
 * いずれの場合も ctx には有効なハンドルが入る。
 */
#define HOSTFXR_ALREADY_INITIALIZED ((int)0x80008060)

static int IsHostfxrInitSuccess(int rc, void* ctx)
{
    /* ctx が非 NULL であれば呼び出し成功とみなす（最も確実な判定） */
    return ctx != NULL && (rc == 0 || rc == 1 || rc == HOSTFXR_ALREADY_INITIALIZED);
}

/* ---- hostfxr 型定義 ---------------------------------------------------- */

typedef void* hostfxr_handle;

typedef enum {
    hdt_com_activation                        = 0,
    hdt_load_in_memory_assembly               = 1,
    hdt_winrt_activation                      = 2,
    hdt_com_register                          = 3,
    hdt_com_unregister                        = 4,
    hdt_load_assembly_and_get_function_pointer = 5,
    hdt_get_function_pointer                  = 6,
} hostfxr_delegate_type;

typedef int (__cdecl *hostfxr_initialize_for_runtime_config_fn)(
    const wchar_t*  runtime_config_path,
    const void*     parameters,
    hostfxr_handle* host_context_handle);

typedef int (__cdecl *hostfxr_get_runtime_delegate_fn)(
    hostfxr_handle        host_context_handle,
    hostfxr_delegate_type type,
    void**                delegate);

typedef int (__cdecl *hostfxr_close_fn)(hostfxr_handle host_context_handle);

/* 標準エントリポイント署名: public static int Method(IntPtr arg, int argSize) */
typedef int (__cdecl *component_entry_point_fn)(void* arg, int arg_size);

typedef int (__cdecl *load_assembly_and_get_function_pointer_fn)(
    const wchar_t* assembly_path,
    const wchar_t* type_name,
    const wchar_t* method_name,
    const wchar_t* delegate_type_name,  /* NULL = 標準シグネチャ */
    void*          reserved,
    void**         delegate);

/* ---- グローバル --------------------------------------------------------- */

static HMODULE g_hSelf = NULL;

/* ---- デバッグログ -------------------------------------------------------- */

static FILE* g_log = NULL;

static void Log(const char* fmt, ...)
{
    if (!g_log) return;
    va_list args;
    va_start(args, fmt);
    fprintf(g_log, "[WpfInspector.Bootstrap] ");
    vfprintf(g_log, fmt, args);
    fprintf(g_log, "\n");
    fflush(g_log);
    va_end(args);
}

/* ---- ユーティリティ ----------------------------------------------------- */

static void GetSiblingPath(const wchar_t* filename, wchar_t* out, size_t cch)
{
    wchar_t buf[MAX_PATH];
    GetModuleFileNameW(g_hSelf, buf, MAX_PATH);
    wchar_t* slash = wcsrchr(buf, L'\\');
    if (slash) *(slash + 1) = L'\0';
    StringCchPrintfW(out, cch, L"%s%s", buf, filename);
}

static void BuildPipeName(char* out, size_t outSize)
{
    char modulePath[MAX_PATH];
    GetModuleFileNameA(g_hSelf, modulePath, MAX_PATH);

    const char* fileName = strrchr(modulePath, '\\');
    fileName = fileName ? fileName + 1 : modulePath;

    const char* prefix = "WpfInspector.NativeBootstrap.";
    const char* suffix = ".dll";

    if (strncmp(fileName, prefix, strlen(prefix)) == 0)
    {
        const char* sessionStart = fileName + strlen(prefix);
        const char* suffixPos = strstr(sessionStart, suffix);
        if (suffixPos && suffixPos > sessionStart)
        {
            char sessionId[64];
            size_t sessionLength = (size_t)(suffixPos - sessionStart);
            if (sessionLength < sizeof(sessionId))
            {
                memcpy(sessionId, sessionStart, sessionLength);
                sessionId[sessionLength] = '\0';
                sprintf_s(out, outSize,
                          "\\\\.\\pipe\\wpfinspector-%u-%s",
                          GetCurrentProcessId(),
                          sessionId);
                return;
            }
        }
    }

    sprintf_s(out, outSize,
              "\\\\.\\pipe\\wpfinspector-%u",
              GetCurrentProcessId());
}

/* ---- 注入スレッド ------------------------------------------------------- */

static DWORD WINAPI BootstrapThread(LPVOID _unused)
{
    /* デバッグログ: %TEMP%\wpfi_bootstrap.log */
    {
        char logPath[MAX_PATH];
        GetTempPathA(MAX_PATH, logPath);
        StringCbCatA(logPath, MAX_PATH, "wpfi_bootstrap.log");
        g_log = fopen(logPath, "w");
    }
    Log("BootstrapThread started (PID=%u)", GetCurrentProcessId());

    /* 1. hostfxr.dll を取得（対象プロセスに既ロード済み） */
    HMODULE hostfxr = GetModuleHandleW(L"hostfxr.dll");
    if (!hostfxr) { Log("ERROR: hostfxr.dll not found"); return 1; }
    Log("hostfxr.dll found");

    hostfxr_initialize_for_runtime_config_fn pfn_init =
        (hostfxr_initialize_for_runtime_config_fn)
        GetProcAddress(hostfxr, "hostfxr_initialize_for_runtime_config");
    hostfxr_get_runtime_delegate_fn pfn_get_delegate =
        (hostfxr_get_runtime_delegate_fn)
        GetProcAddress(hostfxr, "hostfxr_get_runtime_delegate");
    hostfxr_close_fn pfn_close =
        (hostfxr_close_fn)GetProcAddress(hostfxr, "hostfxr_close");

    if (!pfn_init || !pfn_get_delegate || !pfn_close)
    {
        Log("ERROR: hostfxr exports not found (init=%p, get_delegate=%p, close=%p)",
            pfn_init, pfn_get_delegate, pfn_close);
        return 2;
    }

    /* 2. secondary host context を初期化
     *    既に .NET が走っているプロセスでは 0x80008060 (HostApiAlreadyInitialized)
     *    が返るが、これは正常 — ctx には有効なセカンダリコンテキストが入る。 */
    wchar_t runtimeConfigPath[MAX_PATH];
    GetSiblingPath(L"WpfInspector.TestHelper.runtimeconfig.json", runtimeConfigPath, MAX_PATH);
    Log("runtimeconfig: %ls", runtimeConfigPath);

    hostfxr_handle ctx = NULL;
    int rc = pfn_init(runtimeConfigPath, NULL, &ctx);
    Log("pfn_init rc=0x%X (%d), ctx=%p", (unsigned)rc, rc, ctx);
    if (!IsHostfxrInitSuccess(rc, ctx))
    {
        Log("ERROR: pfn_init failed (rc=0x%X, ctx=%p)", (unsigned)rc, ctx);
        return 3;
    }

    /* 3. load_assembly_and_get_function_pointer デリゲートを取得 */
    load_assembly_and_get_function_pointer_fn pfn_load = NULL;
    rc = pfn_get_delegate(ctx, hdt_load_assembly_and_get_function_pointer, (void**)&pfn_load);
    Log("pfn_get_delegate rc=0x%X (%d), pfn_load=%p", (unsigned)rc, rc, pfn_load);
    pfn_close(ctx);
    if (rc != 0 || !pfn_load)
    {
        Log("ERROR: pfn_get_delegate failed");
        return 4;
    }

    /* 4. WpfInspector.TestHelper.dll をロードして EntryPoint.Run を取得 */
    wchar_t assemblyPath[MAX_PATH];
    GetSiblingPath(L"WpfInspector.TestHelper.dll", assemblyPath, MAX_PATH);
    Log("assembly: %ls", assemblyPath);

    component_entry_point_fn pfn_run = NULL;
    rc = pfn_load(
        assemblyPath,
        L"WpfInspector.TestHelper.EntryPoint, WpfInspector.TestHelper",
        L"Run",
        NULL,   /* 標準 component_entry_point_fn */
        NULL,
        (void**)&pfn_run);
    Log("pfn_load rc=0x%X (%d), pfn_run=%p", (unsigned)rc, rc, pfn_run);
    if (rc != 0 || !pfn_run)
    {
        Log("ERROR: pfn_load failed");
        return 5;
    }

    /* 5. パイプ名を構築して InspectionPayload に渡す
     *    名前: \\.\pipe\wpfinspector-{PID}
     *    WpfInspector 側がこの名前でサーバーを立てている。 */
    char pipeName[128];
    BuildPipeName(pipeName, sizeof(pipeName));
    Log("calling EntryPoint.Run with pipe: %s", pipeName);

    int runRc = pfn_run(pipeName, (int)(strlen(pipeName) + 1));
    Log("EntryPoint.Run returned %d", runRc);

    if (g_log) fclose(g_log);
    return 0;
}

/* ---- DllMain ----------------------------------------------------------- */

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID reserved)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(hModule);
        g_hSelf = hModule;
        HANDLE hThread = CreateThread(NULL, 0, BootstrapThread, NULL, 0, NULL);
        if (hThread) CloseHandle(hThread);
    }
    return TRUE;
}
