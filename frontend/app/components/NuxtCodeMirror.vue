<script setup lang="ts">
import type { Extension } from '@codemirror/state'
import { EditorState } from '@codemirror/state'
import { EditorView, keymap, placeholder as placeholderExtension } from '@codemirror/view'
import { indentWithTab as indentWithTabCommand } from '@codemirror/commands'
import { basicSetup as codeMirrorBasicSetup } from 'codemirror'

defineOptions({ inheritAttrs: false })

const props = withDefaults(defineProps<{
  modelValue?: string
  editable?: boolean
  readOnly?: boolean
  placeholder?: string
  theme?: Extension
  basicSetup?: boolean
  indentWithTab?: boolean
  extensions?: Extension[]
}>(), {
  modelValue: '',
  editable: true,
  readOnly: false,
  placeholder: '',
  basicSetup: true,
  indentWithTab: true,
  extensions: () => [],
})

const emit = defineEmits<{
  'update:modelValue': [value: string]
}>()

const editorElement = useTemplateRef<HTMLDivElement>('editorElement')
let editorView: EditorView | undefined

onMounted(() => {
  const extensions: Extension[] = [
    EditorView.updateListener.of((update) => {
      if (update.docChanged) {
        emit('update:modelValue', update.state.doc.toString())
      }
    }),
    EditorView.editable.of(props.editable),
    EditorState.readOnly.of(props.readOnly),
  ]

  if (props.basicSetup) extensions.push(codeMirrorBasicSetup)
  if (props.indentWithTab) extensions.push(keymap.of([indentWithTabCommand]))
  if (props.placeholder) extensions.push(placeholderExtension(props.placeholder))
  if (props.theme) extensions.push(props.theme)
  extensions.push(...props.extensions)

  editorView = new EditorView({
    parent: editorElement.value!,
    state: EditorState.create({
      doc: props.modelValue,
      extensions,
    }),
  })
})

watch(() => props.modelValue, (value) => {
  if (!editorView || value === editorView.state.doc.toString()) return

  editorView.dispatch({
    changes: {
      from: 0,
      to: editorView.state.doc.length,
      insert: value,
    },
  })
})

onBeforeUnmount(() => editorView?.destroy())
</script>

<template>
  <div ref="editorElement" v-bind="$attrs" />
</template>
