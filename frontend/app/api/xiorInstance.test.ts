import { beforeEach, describe, expect, it, vi } from "vitest"

const mocks = vi.hoisted(() => {
  const clients: Array<{
    requestHandler?: (config: Record<string, unknown>) => unknown
    responseErrorHandler?: (error: unknown) => unknown
    interceptors: {
      request: {
        use: ReturnType<typeof vi.fn>
      }
      response: {
        use: ReturnType<typeof vi.fn>
      }
    }
  }> = []

  const create = vi.fn(() => {
    const client: {
      requestHandler?: (config: Record<string, unknown>) => unknown
      responseErrorHandler?: (error: unknown) => unknown
      interceptors: {
        request: { use: ReturnType<typeof vi.fn> }
        response: { use: ReturnType<typeof vi.fn> }
      }
    } = {
      interceptors: {
        request: {
          use: vi.fn(),
        },
        response: {
          use: vi.fn(),
        },
      },
    }

    client.interceptors.request.use.mockImplementation(
      (handler: (config: Record<string, unknown>) => unknown) => {
        client.requestHandler = handler
      },
    )
    client.interceptors.response.use.mockImplementation(
      (
        _success: (response: unknown) => unknown,
        error: (failure: unknown) => unknown,
      ) => {
        client.responseErrorHandler = error
      },
    )

    clients.push(client)
    return client
  })

  return {
    clients,
    create,
    request: vi.fn(),
    refreshToken: vi.fn(),
    signOut: vi.fn(),
    toastError: vi.fn(),
    persistAuthSession: vi.fn(),
    clearAuthSession: vi.fn(),
    navigateTo: vi.fn(),
  }
})

vi.mock("xior", () => ({
  default: {
    create: mocks.create,
    request: mocks.request,
  },
}))

vi.mock("./auth", () => ({
  refreshToken: mocks.refreshToken,
  signOut: mocks.signOut,
}))

vi.mock("vue-sonner", () => ({
  toast: {
    error: mocks.toastError,
  },
}))

vi.mock("@/lib/auth-session", () => ({
  persistAuthSession: mocks.persistAuthSession,
  clearAuthSession: mocks.clearAuthSession,
}))

await import("./xiorInstance")

describe("authenticated xior interceptors", () => {
  beforeEach(() => {
    localStorage.clear()
    vi.clearAllMocks()
    vi.stubGlobal("navigateTo", mocks.navigateTo)
  })

  it("adds the current access token to authenticated requests", () => {
    localStorage.setItem("accessToken", "access-1")
    const tokenClient = mocks.clients[1]
    const config = { headers: {} as Record<string, string> }

    const result = tokenClient.requestHandler?.(config)

    expect(result).toBe(config)
    expect(config.headers.Authorization).toBe("Bearer access-1")
  })

  it("adds the current refresh token to refresh requests", () => {
    localStorage.setItem("refreshToken", "refresh-1")
    const refreshClient = mocks.clients[2]
    const config = { headers: {} as Record<string, string> }

    refreshClient.requestHandler?.(config)

    expect(config.headers.Authorization).toBe("Bearer refresh-1")
  })

  it("shares a single refresh request across concurrent 401 responses", async () => {
    let resolveRefresh: ((value: { accessToken: string }) => void) | undefined
    mocks.refreshToken.mockReturnValue(
      new Promise<{ accessToken: string }>((resolve) => {
        resolveRefresh = resolve
      }),
    )
    mocks.request.mockResolvedValue({ ok: true })

    const tokenClient = mocks.clients[1]
    const first = tokenClient.responseErrorHandler?.({
      response: { status: 401 },
      config: { url: "/first", headers: { Existing: "one" } },
    }) as Promise<unknown>
    const second = tokenClient.responseErrorHandler?.({
      response: { status: 401 },
      config: { url: "/second", headers: { Existing: "two" } },
    }) as Promise<unknown>

    expect(mocks.refreshToken).toHaveBeenCalledTimes(1)

    resolveRefresh?.({ accessToken: "access-2" })
    await Promise.all([first, second])

    expect(mocks.persistAuthSession).toHaveBeenCalledWith({
      accessToken: "access-2",
    })
    expect(mocks.request).toHaveBeenCalledTimes(2)
    expect(mocks.request).toHaveBeenCalledWith(
      expect.objectContaining({
        url: "/first",
        headers: expect.objectContaining({
          Existing: "one",
          Authorization: "Bearer access-2",
        }),
      }),
    )
    expect(mocks.request).toHaveBeenCalledWith(
      expect.objectContaining({
        url: "/second",
        headers: expect.objectContaining({
          Existing: "two",
          Authorization: "Bearer access-2",
        }),
      }),
    )
  })

  it("does not retry a 401 when refresh returns no access token", async () => {
    mocks.refreshToken.mockResolvedValue({ refreshToken: "refresh-2" })
    const error = {
      response: { status: 401 },
      config: { url: "/protected", headers: {} },
    }

    await expect(
      mocks.clients[1].responseErrorHandler?.(error),
    ).rejects.toBe(error)
    expect(mocks.request).not.toHaveBeenCalled()
    expect(mocks.persistAuthSession).not.toHaveBeenCalled()
  })

  it("surfaces 403 responses without attempting refresh", async () => {
    const error = { response: { status: 403 } }

    await expect(
      mocks.clients[1].responseErrorHandler?.(error),
    ).rejects.toBe(error)

    expect(mocks.toastError).toHaveBeenCalledWith("Permission denied.")
    expect(mocks.refreshToken).not.toHaveBeenCalled()
  })

  it("clears the session and returns to login when refresh fails", async () => {
    mocks.signOut.mockResolvedValue(undefined)
    mocks.navigateTo.mockResolvedValue(undefined)
    const error = {
      response: {
        status: 401,
        data: { message: "Session revoked." },
      },
    }

    await expect(
      mocks.clients[2].responseErrorHandler?.(error),
    ).rejects.toBe(error)

    expect(mocks.toastError).toHaveBeenCalledWith("Session revoked.")
    expect(mocks.signOut).toHaveBeenCalledTimes(1)
    expect(mocks.clearAuthSession).toHaveBeenCalledTimes(1)
    expect(mocks.navigateTo).toHaveBeenCalledWith("/login")
  })
})
