import { describe, it, expect } from 'vitest'
import { mount } from '@vue/test-utils'
import { defineComponent } from 'vue'

vi.mock('@/api/db-management', () => ({
  getSchemas: vi.fn().mockResolvedValue([]),
  getTables: vi.fn().mockResolvedValue([]),
  getColumns: vi.fn().mockResolvedValue([]),
}))

import { useSqlBuilder, type WhereItem } from './useSqlBuilder'

function createBuilder(options: { type: 'Query' | 'DML' }) {
  const wrapper = mount(
    defineComponent({
      setup() {
        const builder = useSqlBuilder(options)
        return { builder }
      },
      template: '<div></div>',
    }),
  )
  return (wrapper.vm as any).builder as ReturnType<typeof useSqlBuilder>
}

describe('useSqlBuilder', () => {
  describe('generateJson - Query', () => {
    it('builds basic SELECT with WHERE condition', () => {
      const builder = createBuilder({ type: 'Query' })
      builder.table.value = 'users'

      builder.whereConditions.value = [
        {
          type: 'basic',
          table: 'users',
          field: 'age',
          operator: '>',
          value: '18',
          isOr: false,
          isNot: false,
          isDate: false,
          leftTable: '',
          leftField: '',
          rightTable: '',
          rightField: '',
          values: '',
        } as WhereItem,
      ]

      const json = JSON.parse(builder.generateJson())
      expect(json.tableName).toBe('users')
      expect(json.whereColumnsAndValues).toHaveLength(1)
      expect(json.whereColumnsAndValues[0]).toMatchObject({
        type: 'basic',
        fieldName: 'users.age',
        operator: '>',
        value: '18',
      })
    })

    it('handles IN operator with comma-separated values', () => {
      const builder = createBuilder({ type: 'Query' })
      builder.table.value = 'users'

      builder.whereConditions.value = [
        {
          type: 'basic',
          table: 'users',
          field: 'status',
          operator: 'IN',
          value: '',
          values: 'active, pending',
          isOr: false,
          isNot: false,
          isDate: false,
          leftTable: '',
          leftField: '',
          rightTable: '',
          rightField: '',
        } as WhereItem,
      ]

      const json = JSON.parse(builder.generateJson())
      expect(json.whereColumnsAndValues[0]).toMatchObject({
        type: 'basic',
        fieldName: 'users.status',
        operator: 'IN',
        values: ['active', 'pending'],
      })
    })

    it('builds column_compare condition', () => {
      const builder = createBuilder({ type: 'Query' })
      builder.table.value = 'orders'

      builder.whereConditions.value = [
        {
          type: 'column_compare',
          table: '',
          field: '',
          operator: '=',
          value: '',
          isOr: false,
          isNot: false,
          isDate: false,
          leftTable: 'orders',
          leftField: 'user_id',
          rightTable: 'users',
          rightField: 'id',
          values: '',
        } as WhereItem,
      ]

      const json = JSON.parse(builder.generateJson())
      expect(json.whereColumnsAndValues[0]).toMatchObject({
        type: 'column_compare',
        leftFieldName: 'orders.user_id',
        operator: '=',
        rightFieldName: 'users.id',
      })
    })

    it('propagates isOr and isNot flags in WHERE', () => {
      const builder = createBuilder({ type: 'Query' })
      builder.table.value = 'users'

      builder.whereConditions.value = [
        {
          type: 'basic',
          table: 'users',
          field: 'name',
          operator: '=',
          value: 'Alice',
          isOr: true,
          isNot: true,
          isDate: false,
          leftTable: '',
          leftField: '',
          rightTable: '',
          rightField: '',
          values: '',
        } as WhereItem,
      ]

      const json = JSON.parse(builder.generateJson())
      expect(json.whereColumnsAndValues[0].isOr).toBe(true)
      expect(json.whereColumnsAndValues[0].isNot).toBe(true)
    })

    it('builds select columns', () => {
      const builder = createBuilder({ type: 'Query' })
      builder.table.value = 'orders'
      builder.selectColumns.value = [
        { type: 'field', table: 'orders', field: 'id', alias: '', constant: '', functionName: '', isDistinct: false, arguments: [] },
        { type: 'field', table: 'orders', field: 'total', alias: 'amount', constant: '', functionName: '', isDistinct: false, arguments: [] },
      ]

      const json = JSON.parse(builder.generateJson())
      expect(json.selectColumns).toHaveLength(2)
      expect(json.selectColumns[0]).toMatchObject({ type: 'field', fieldName: 'orders.id' })
      expect(json.selectColumns[1]).toMatchObject({ type: 'field', fieldName: 'orders.total', alias: 'amount' })
    })

    it('generates default selectColumns when none provided', () => {
      const builder = createBuilder({ type: 'Query' })
      builder.table.value = 'products'
      builder.selectColumns.value = []

      const json = JSON.parse(builder.generateJson())
      expect(json.selectColumns).toHaveLength(1)
      expect(json.selectColumns[0]).toMatchObject({ type: 'field', fieldName: '*' })
    })
  })

  describe('generateJson - DML', () => {
    it('builds INSERT with values', () => {
      const builder = createBuilder({ type: 'DML' })
      builder.table.value = 'users'
      builder.insertValues.value = [
        { fieldName: 'name', value: '{{name}}' },
        { fieldName: 'email', value: 'test@example.com' },
      ]

      const json = JSON.parse(builder.generateJson())
      expect(json.operation).toBe('Insert')
      expect(json.tableName).toBe('users')
      expect(json.values).toHaveLength(2)
      expect(json.values[0]).toMatchObject({ fieldName: 'name' })
    })

    it('builds INSERT without values and WHERE', () => {
      const builder = createBuilder({ type: 'DML' })
      builder.table.value = 'users'

      const json = JSON.parse(builder.generateJson())
      expect(json.operation).toBe('Insert')
      expect(json.tableName).toBe('users')
      expect(json.values).toBeUndefined()
    })
  })
})
