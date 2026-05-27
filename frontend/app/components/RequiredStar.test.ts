import { describe, it, expect } from 'vitest'
import { mount } from '@vue/test-utils'
import RequiredStar from './RequiredStar.vue'

describe('RequiredStar', () => {
  it('renders a red asterisk', () => {
    const wrapper = mount(RequiredStar)
    expect(wrapper.text()).toBe('*')
    expect(wrapper.element.tagName).toBe('SPAN')
  })
})
