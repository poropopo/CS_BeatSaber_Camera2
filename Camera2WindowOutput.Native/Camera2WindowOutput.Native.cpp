#include <Windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <condition_variable>
#include <cstdint>
#include <exception>
#include <functional>
#include <future>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <unordered_map>
#include <wrl/client.h>

using Microsoft::WRL::ComPtr;

#ifndef UNITY_INTERFACE_API
#define UNITY_INTERFACE_API __stdcall
#endif

namespace {
	const wchar_t* WindowClassName = L"Camera2WindowOutputWindow";
	const wchar_t* ControlWindowClassName = L"Camera2WindowOutputControlWindow";
	constexpr UINT WM_UI_TASK = WM_APP + 1;
	constexpr UINT WM_UI_STOP = WM_APP + 2;
	constexpr int FixedWidth = 1920;
	constexpr int FixedHeight = 1080;
	constexpr DWORD FixedWindowStyle = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;

	struct Output {
		int handle = 0;
		HWND hwnd = nullptr;
		bool closeRequested = false;
		bool visible = true;
		int width = FixedWidth;
		int height = FixedHeight;
		ComPtr<ID3D11Texture2D> source;
		ComPtr<ID3D11Device> device;
		ComPtr<ID3D11DeviceContext> context;
		ComPtr<IDXGISwapChain> swapChain;
	};

	std::mutex g_mutex;
	std::unordered_map<int, std::shared_ptr<Output>> g_outputs;
	int g_nextHandle = 1;
	HINSTANCE g_instance = nullptr;

	std::mutex g_uiMutex;
	std::condition_variable g_uiReady;
	std::thread* g_uiThread = nullptr;
	DWORD g_uiThreadId = 0;
	HWND g_controlWindow = nullptr;
	bool g_uiStartupComplete = false;

	struct UiTask {
		std::function<void()> action;
		std::promise<void> completed;
	};

	Output* FindOutputLocked(int handle) {
		auto it = g_outputs.find(handle);
		return it == g_outputs.end() ? nullptr : it->second.get();
	}

	std::shared_ptr<Output> FindOutputRefLocked(int handle) {
		auto it = g_outputs.find(handle);
		return it == g_outputs.end() ? nullptr : it->second;
	}

	LRESULT CALLBACK WindowProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) {
		if(message == WM_NCCREATE) {
			auto createStruct = reinterpret_cast<CREATESTRUCTW*>(lParam);
			SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(createStruct->lpCreateParams));
		}

		if(message == WM_CLOSE) {
			auto* output = reinterpret_cast<Output*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
			if(output) {
				{
					std::lock_guard<std::mutex> lock(g_mutex);
					output->closeRequested = true;
					output->visible = false;
				}
				ShowWindow(hwnd, SW_HIDE);
				return 0;
			}
		}

		if(message == WM_NCDESTROY)
			SetWindowLongPtrW(hwnd, GWLP_USERDATA, 0);

		if(message == WM_DESTROY)
			return 0;

		return DefWindowProcW(hwnd, message, wParam, lParam);
	}

	LRESULT CALLBACK ControlWindowProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) {
		(void)hwnd;
		(void)wParam;

		if(message == WM_UI_TASK) {
			std::unique_ptr<UiTask> task(reinterpret_cast<UiTask*>(lParam));
			try {
				task->action();
				task->completed.set_value();
			} catch(...) {
				task->completed.set_exception(std::current_exception());
			}
			return 0;
		}

		if(message == WM_UI_STOP) {
			DestroyWindow(g_controlWindow);
			PostQuitMessage(0);
			return 0;
		}

		return DefWindowProcW(hwnd, message, wParam, lParam);
	}

	void RegisterWindowClasses() {
		static bool registered = false;
		if(registered)
			return;

		WNDCLASSEXW outputWindowClass = {};
		outputWindowClass.cbSize = sizeof(outputWindowClass);
		outputWindowClass.lpfnWndProc = WindowProc;
		outputWindowClass.hInstance = g_instance;
		outputWindowClass.hCursor = LoadCursor(nullptr, IDC_ARROW);
		outputWindowClass.lpszClassName = WindowClassName;

		WNDCLASSEXW controlWindowClass = {};
		controlWindowClass.cbSize = sizeof(controlWindowClass);
		controlWindowClass.lpfnWndProc = ControlWindowProc;
		controlWindowClass.hInstance = g_instance;
		controlWindowClass.lpszClassName = ControlWindowClassName;

		RegisterClassExW(&outputWindowClass);
		RegisterClassExW(&controlWindowClass);
		registered = true;
	}

	bool CreateSwapChain(Output& output) {
		if(output.swapChain || !output.device || !output.hwnd)
			return output.swapChain != nullptr;

		ComPtr<IDXGIDevice> dxgiDevice;
		ComPtr<IDXGIAdapter> adapter;
		ComPtr<IDXGIFactory> factory;

		if(FAILED(output.device.As(&dxgiDevice)) ||
		   FAILED(dxgiDevice->GetAdapter(&adapter)) ||
		   FAILED(adapter->GetParent(__uuidof(IDXGIFactory), reinterpret_cast<void**>(factory.GetAddressOf()))))
			return false;

		DXGI_SWAP_CHAIN_DESC desc = {};
		desc.BufferDesc.Width = output.width;
		desc.BufferDesc.Height = output.height;
		desc.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
		desc.SampleDesc.Count = 1;
		desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
		desc.BufferCount = 2;
		desc.OutputWindow = output.hwnd;
		desc.Windowed = TRUE;
		desc.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;

		return SUCCEEDED(factory->CreateSwapChain(output.device.Get(), &desc, output.swapChain.GetAddressOf()));
	}

	void UiThreadMain() {
		RegisterWindowClasses();

		HWND controlWindow = CreateWindowExW(
			0,
			ControlWindowClassName,
			L"Camera2WindowOutputControl",
			0,
			0,
			0,
			0,
			0,
			HWND_MESSAGE,
			nullptr,
			g_instance,
			nullptr
		);

		{
			std::lock_guard<std::mutex> lock(g_uiMutex);
			g_uiThreadId = GetCurrentThreadId();
			g_controlWindow = controlWindow;
			g_uiStartupComplete = true;
		}
		g_uiReady.notify_all();

		if(!controlWindow)
			return;

		MSG msg = {};
		while(GetMessageW(&msg, nullptr, 0, 0) > 0) {
			TranslateMessage(&msg);
			DispatchMessageW(&msg);
		}

		{
			std::lock_guard<std::mutex> lock(g_uiMutex);
			if(g_controlWindow == controlWindow)
				g_controlWindow = nullptr;
			g_uiThreadId = 0;
			g_uiStartupComplete = false;
		}
	}

	void StopUiThread() {
		std::thread* uiThread = nullptr;
		HWND controlWindow = nullptr;

		{
			std::lock_guard<std::mutex> lock(g_uiMutex);
			uiThread = g_uiThread;
			controlWindow = g_controlWindow;
		}

		if(!uiThread)
			return;

		if(controlWindow)
			PostMessageW(controlWindow, WM_UI_STOP, 0, 0);

		if(uiThread->joinable() && uiThread->get_id() != std::this_thread::get_id())
			uiThread->join();
		else if(uiThread->joinable())
			uiThread->detach();

		{
			std::lock_guard<std::mutex> lock(g_uiMutex);
			if(g_uiThread == uiThread)
				g_uiThread = nullptr;
			g_controlWindow = nullptr;
			g_uiThreadId = 0;
			g_uiStartupComplete = false;
		}

		delete uiThread;
	}

	bool EnsureUiThread() {
		{
			std::lock_guard<std::mutex> lock(g_uiMutex);
			if(g_controlWindow)
				return true;
		}

		std::unique_lock<std::mutex> lock(g_uiMutex);
		if(!g_uiThread) {
			g_uiStartupComplete = false;
			try {
				g_uiThread = new std::thread(UiThreadMain);
			} catch(...) {
				g_uiThread = nullptr;
				g_uiStartupComplete = true;
				return false;
			}
		}

		g_uiReady.wait(lock, [] { return g_uiStartupComplete; });
		const bool started = g_controlWindow != nullptr;
		lock.unlock();

		if(!started)
			StopUiThread();

		return started;
	}

	bool PostUiTask(std::function<void()> action, bool wait) {
		if(!EnsureUiThread())
			return false;

		DWORD uiThreadId = 0;
		HWND controlWindow = nullptr;
		{
			std::lock_guard<std::mutex> lock(g_uiMutex);
			uiThreadId = g_uiThreadId;
			controlWindow = g_controlWindow;
		}

		if(!controlWindow)
			return false;

		if(GetCurrentThreadId() == uiThreadId) {
			action();
			return true;
		}

		auto task = std::make_unique<UiTask>();
		task->action = std::move(action);
		auto completed = task->completed.get_future();

		if(!PostMessageW(controlWindow, WM_UI_TASK, 0, reinterpret_cast<LPARAM>(task.get())))
			return false;

		task.release();

		if(wait)
			completed.get();

		return true;
	}

	void StopUiThreadIfIdle() {
		bool idle = false;
		{
			std::lock_guard<std::mutex> lock(g_mutex);
			idle = g_outputs.empty();
		}

		if(idle)
			StopUiThread();
	}

	void UNITY_INTERFACE_API OnRenderEvent(int eventId) {
		std::lock_guard<std::mutex> lock(g_mutex);
		auto* output = FindOutputLocked(eventId);
		if(!output)
			return;

		if(output->closeRequested || !output->visible || !output->source || !output->hwnd)
			return;

		if(!output->device) {
			output->source->GetDevice(output->device.GetAddressOf());
			if(output->device)
				output->device->GetImmediateContext(output->context.GetAddressOf());
		}

		if(!CreateSwapChain(*output))
			return;

		ComPtr<ID3D11Texture2D> backBuffer;
		if(FAILED(output->swapChain->GetBuffer(0, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(backBuffer.GetAddressOf()))))
			return;

		output->context->CopyResource(backBuffer.Get(), output->source.Get());
		output->swapChain->Present(0, 0);
	}
}

extern "C" {
	typedef void(__stdcall* UnityRenderingEvent)(int eventId);
	__declspec(dllexport) void __cdecl DestroyWindowOutput(int handle);

	__declspec(dllexport) int __cdecl CreateWindowOutput(const wchar_t* title, int width, int height) {
		if(!EnsureUiThread())
			return 0;

		auto output = std::make_shared<Output>();
		output->width = width > 0 ? width : FixedWidth;
		output->height = height > 0 ? height : FixedHeight;

		const std::wstring windowTitle = title == nullptr ? L"Camera2" : title;
		int handle = 0;
		{
			std::lock_guard<std::mutex> lock(g_mutex);
			handle = g_nextHandle++;
			output->handle = handle;
			g_outputs.emplace(handle, output);
		}

		const bool created = PostUiTask([output, windowTitle] {
			int outputWidth = FixedWidth;
			int outputHeight = FixedHeight;
			bool visible = false;

			{
				std::lock_guard<std::mutex> lock(g_mutex);
				outputWidth = output->width;
				outputHeight = output->height;
				visible = output->visible && !output->closeRequested;
			}

			RECT rect = { 0, 0, outputWidth, outputHeight };
			AdjustWindowRect(&rect, FixedWindowStyle, FALSE);

			HWND hwnd = CreateWindowExW(
				0,
				WindowClassName,
				windowTitle.c_str(),
				FixedWindowStyle,
				CW_USEDEFAULT,
				CW_USEDEFAULT,
				rect.right - rect.left,
				rect.bottom - rect.top,
				nullptr,
				nullptr,
				g_instance,
				output.get()
			);

			if(!hwnd)
				return;

			{
				std::lock_guard<std::mutex> lock(g_mutex);
				output->hwnd = hwnd;
				visible = output->visible && !output->closeRequested;
			}

			ShowWindow(hwnd, visible ? SW_SHOW : SW_HIDE);
		}, true);

		bool hasWindow = false;
		{
			std::lock_guard<std::mutex> lock(g_mutex);
			hasWindow = output->hwnd != nullptr;
		}

		if(!created || !hasWindow) {
			DestroyWindowOutput(handle);
			return 0;
		}

		return handle;
	}

	__declspec(dllexport) void __cdecl DestroyWindowOutput(int handle) {
		std::shared_ptr<Output> output;
		{
			std::lock_guard<std::mutex> lock(g_mutex);
			auto it = g_outputs.find(handle);
			if(it == g_outputs.end())
				return;
			output = it->second;
			output->visible = false;
			output->closeRequested = true;
			output->source.Reset();
			output->device.Reset();
			output->context.Reset();
			output->swapChain.Reset();
			g_outputs.erase(it);
		}

		PostUiTask([output] {
			HWND hwnd = nullptr;
			{
				std::lock_guard<std::mutex> lock(g_mutex);
				hwnd = output->hwnd;
				output->hwnd = nullptr;
			}

			if(hwnd)
				DestroyWindow(hwnd);
		}, true);

		StopUiThreadIfIdle();
	}

	__declspec(dllexport) void __cdecl SetSourceTexture(int handle, void* texture, int width, int height) {
		(void)width;
		(void)height;

		std::lock_guard<std::mutex> lock(g_mutex);
		auto* output = FindOutputLocked(handle);
		if(!output)
			return;

		output->source.Reset();
		output->device.Reset();
		output->context.Reset();
		output->swapChain.Reset();

		if(texture)
			reinterpret_cast<ID3D11Texture2D*>(texture)->QueryInterface(__uuidof(ID3D11Texture2D), reinterpret_cast<void**>(output->source.GetAddressOf()));
	}

	__declspec(dllexport) void __cdecl SetVisible(int handle, bool visible) {
		std::shared_ptr<Output> output;
		{
			std::lock_guard<std::mutex> lock(g_mutex);
			output = FindOutputRefLocked(handle);
			if(!output)
				return;

			output->visible = visible;
		}

		PostUiTask([output] {
			HWND hwnd = nullptr;
			bool shouldShow = false;
			{
				std::lock_guard<std::mutex> lock(g_mutex);
				hwnd = output->hwnd;
				shouldShow = output->visible && !output->closeRequested;
			}

			if(hwnd)
				ShowWindow(hwnd, shouldShow ? SW_SHOW : SW_HIDE);
		}, false);
	}

	__declspec(dllexport) void __cdecl SetWindowTitle(int handle, const wchar_t* title) {
		std::shared_ptr<Output> output;
		{
			std::lock_guard<std::mutex> lock(g_mutex);
			output = FindOutputRefLocked(handle);
			if(!output)
				return;
		}

		const std::wstring windowTitle = title == nullptr ? L"Camera2" : title;
		PostUiTask([output, windowTitle] {
			HWND hwnd = nullptr;
			{
				std::lock_guard<std::mutex> lock(g_mutex);
				hwnd = output->hwnd;
			}

			if(hwnd)
				SetWindowTextW(hwnd, windowTitle.c_str());
		}, false);
	}

	__declspec(dllexport) bool __cdecl IsCloseRequested(int handle) {
		std::lock_guard<std::mutex> lock(g_mutex);
		auto* output = FindOutputLocked(handle);
		return output == nullptr || output->closeRequested;
	}

	__declspec(dllexport) UnityRenderingEvent __cdecl GetRenderEventFunc() {
		return OnRenderEvent;
	}
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID) {
	if(reason == DLL_PROCESS_ATTACH) {
		g_instance = module;
		DisableThreadLibraryCalls(module);
	} else if(reason == DLL_PROCESS_DETACH) {
		StopUiThread();
	}
	return TRUE;
}
