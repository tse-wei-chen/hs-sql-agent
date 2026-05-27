import { describe, it, expect } from 'vitest'

function getAuthRedirect(toPath: string, token: string | null): string | null {
  const isLogin = !!token
  const authRoutes = ['/login', '/sign-up']
  if (!isLogin && !authRoutes.includes(toPath)) {
    return '/login'
  }
  if (isLogin && [...authRoutes, '/'].includes(toPath)) {
    return '/home'
  }
  return null
}

describe('auth middleware redirect logic', () => {
  it('redirects unauthenticated users to login for protected routes', () => {
    expect(getAuthRedirect('/home', null)).toBe('/login')
    expect(getAuthRedirect('/runtime/custom-tools', null)).toBe('/login')
    expect(getAuthRedirect('/runtime/db-management', null)).toBe('/login')
  })

  it('allows unauthenticated access to auth routes', () => {
    expect(getAuthRedirect('/login', null)).toBeNull()
    expect(getAuthRedirect('/sign-up', null)).toBeNull()
  })

  it('redirects authenticated users away from login to home', () => {
    expect(getAuthRedirect('/login', 'valid-token')).toBe('/home')
  })

  it('redirects authenticated users away from root to home', () => {
    expect(getAuthRedirect('/', 'valid-token')).toBe('/home')
  })

  it('redirects authenticated users away from sign-up to home', () => {
    expect(getAuthRedirect('/sign-up', 'valid-token')).toBe('/home')
  })

  it('allows authenticated access to protected routes', () => {
    expect(getAuthRedirect('/home', 'valid-token')).toBeNull()
    expect(getAuthRedirect('/runtime/custom-tools', 'valid-token')).toBeNull()
    expect(getAuthRedirect('/runtime/audit', 'valid-token')).toBeNull()
  })
})
