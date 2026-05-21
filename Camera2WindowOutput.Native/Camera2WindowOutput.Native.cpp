#include <Windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <cstdint>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>
#include <wrl/client.h>

using Microsoft::WRL::ComPtr;

#ifndef UNITY_INTERFACE_API
#define UNITY_INTERFACE_API __stdcall
#endif

namespace {
	const wchar_t* WindowClassName = L"Camera2WindowOutputWindow";
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
	std::unordered_map<int, std::unique_ptr<Output>> g_outputs;
	int g_nextHandle = 1;
	HINSTANCE g_instance = nullptr;

	Output* FindOutputLocked(int handle) {
		auto it = g_outputs.find(handle);
		return it == g_outputs.end() ? nullptr : it->second.get();
	}

	LRESULT CALLBACK WindowProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) {
		if(message == WM_CLOSE) {
			std::lock_guard<std::mutex> lock(g_mutex);
			for(auto& entry : g_outputs) {
				if(entry.second->hwnd == hwnd) {
					entry.second->closeRequested = true;
					ShowWindow(hwnd, SW_HIDE);
					return 0;
				}
			}
		}

		if(message == WM_DESTROY)
			return 0;

		return DefWindowProcW(hwnd, message, wParam, lParam);
	}

	void RegisterWindowClass() {
		static bool registered = false;
		if(registered)
			return;

		WNDCLASSEXW wc = {};
		wc.cbSize = sizeof(wc);
		wc.lpfnWndProc = WindowProc;
		wc.hInstance = g_instance;
		wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
		wc.lpszClassName = WindowClassName;

		RegisterClassExW(&wc);
		registered = true;
	}

	bool CreateSwapChain(Output& output) {
		if(output.swapChain || !output.device)
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

	void PumpMessages() {
		MSG msg = {};
		while(PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
			TranslateMessage(&msg);
			DispatchMessageW(&msg);
		}
	}

	void UNITY_INTERFACE_API OnRenderEvent(int eventId) {
		Output* output = nullptr;

		{
			std::lock_guard<std::mutex> lock(g_mutex);
			output = FindOutputLocked(eventId);
			if(!output)
				return;
		}

		PumpMessages();

		if(output->closeRequested || !output->visible || !output->source)
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

	__declspec(dllexport) int __cdecl CreateWindowOutput(const wchar_t* title, int width, int height) {
		RegisterWindowClass();

		auto output = std::make_unique<Output>();
		output->width = width > 0 ? width : FixedWidth;
		output->height = height > 0 ? height : FixedHeight;

		RECT rect = { 0, 0, output->width, output->height };
		AdjustWindowRect(&rect, FixedWindowStyle, FALSE);

		output->hwnd = CreateWindowExW(
			0,
			WindowClassName,
			title == nullptr ? L"Camera2" : title,
			FixedWindowStyle,
			CW_USEDEFAULT,
			CW_USEDEFAULT,
			rect.right - rect.left,
			rect.bottom - rect.top,
			nullptr,
			nullptr,
			g_instance,
			nullptr
		);

		if(!output->hwnd)
			return 0;

		ShowWindow(output->hwnd, SW_SHOW);

		std::lock_guard<std::mutex> lock(g_mutex);
		const int handle = g_nextHandle++;
		output->handle = handle;
		g_outputs.emplace(handle, std::move(output));
		return handle;
	}

	__declspec(dllexport) void __cdecl DestroyWindowOutput(int handle) {
		std::unique_ptr<Output> output;
		{
			std::lock_guard<std::mutex> lock(g_mutex);
			auto it = g_outputs.find(handle);
			if(it == g_outputs.end())
				return;
			output = std::move(it->second);
			g_outputs.erase(it);
		}

		if(output->hwnd)
			DestroyWindow(output->hwnd);
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
		std::lock_guard<std::mutex> lock(g_mutex);
		auto* output = FindOutputLocked(handle);
		if(!output)
			return;

		output->visible = visible;
		ShowWindow(output->hwnd, visible && !output->closeRequested ? SW_SHOW : SW_HIDE);
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
	if(reason == DLL_PROCESS_ATTACH)
		g_instance = module;
	return TRUE;
}
