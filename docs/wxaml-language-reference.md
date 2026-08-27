# WXAML 语言参考

WXAML 是用于声明页面和可复用组件的 XML 语言。它使用 `.wxaml` 描述视图，使用同名 `.js` 文件描述状态和行为。本文是语言的规范性参考；它描述已实现的语法与编译时规则，不描述内部生成代码或运行时私有接口。

## 目录

1. [程序与文件](#程序与文件)
2. [词法和 XML 结构](#词法和-xml-结构)
3. [元素](#元素)
4. [属性和值](#属性和值)
5. [绑定和表达式](#绑定和表达式)
6. [条件与列表](#条件与列表)
7. [组件](#组件)
8. [样式和媒体查询](#样式和媒体查询)
9. [常量子树](#常量子树)
10. [代码文件](#代码文件)
11. [诊断](#诊断)

## 程序与文件

一个项目的源文件位于配置的源目录中。页面采用固定的同名配对：

```text
src/
  app.js
  app.wxaml                 # 可选；仅声明应用级样式
  pages/
    home/
      home.wxaml
      home.js
  components/
    Notice/
      Notice.wxaml
      Notice.js
```

- 页面根元素为 `Page`。构建器优先读取 `manifest.yaml` 的 `router.pages`；每个页面键的最后一段对应 `src/pages/<名称>/<名称>.wxaml`。
- 可复用组件根元素为 `Component`，只能通过 `Import` 使用。
- 页面或组件的 `.js` 文件缺失时，编译器报告错误。
- `x:Class` 可省略；省略时由文件名推断。显式名称必须是有效的 JavaScript 标识符。

## 词法和 XML 结构

WXAML 使用严格的 XML 结构。元素和属性名称不区分大小写，但推荐使用本参考中的 PascalCase 形式。页面和组件文件分别以一个 `Page` 或 `Component` 为根；仅供样式导入的资源文件则以 `ResourceDictionary`、`Styles`、`Page.Styles` 或 `Component.Styles` 为根。

```xml
<Page x:Class="Home">
  <Page.Styles>
    <Style Class="page">
      <Setter Property="padding" Value="16px" />
    </Style>
  </Page.Styles>

  <Div Class="page">
    <Text Text="Hello" />
  </Div>
</Page>
```

项目配置只能位于项目根目录的 `manifest.yaml`。`manifest.json`、`manifest.yml` 和位于源目录中的 manifest 都不是有效的源配置。构建时会从 YAML 生成设备包所需的输出 manifest；生成目录不是源文件编辑位置。

### 编译器配置

`config.minifyIdentifiers` 控制标识符短名化 pass，默认值为 `true`：

```yaml
config:
  logLevel: log
  designWidth: device-width
  minifyIdentifiers: true
```

启用时，编译器会压缩页面、组件及 inline 组件合并后的普通方法名，并在生成 JSC 时压缩函数局部变量和参数名。关闭它可使生成的 JavaScript/JSC 保留这些源名称，便于调试、排查兼容性问题或对照源码：

```yaml
config:
  minifyIdentifiers: false
```

这是仅在构建期读取的配置，不会写入输出目录的运行时 `manifest.json`。即使启用，动态 `this[name]` 访问的组件也会保守地保留方法名。

`Page.Styles` 或 `Component.Styles` 必须位于所有内容节点之前。`Import` 是根元素的直接子元素；建议放在样式段之后、内容之前，放在内容之后会产生警告。

注释使用标准 XML 注释：`<!-- comment -->`。

## 元素

### 原生元素

原生元素以目标平台支持的元素集合为准。常用元素包括：

| 分类 | 元素 |
| --- | --- |
| 容器 | `Div`、`Stack`、`Scroll`、`List`、`Swiper`、`Tabs` |
| 文本 | `Text`、`Span`、`Label`、`RichText` |
| 媒体 | `Image`、`Video`、`Lottie`、`Canvas` |
| 输入 | `Input`、`Textarea`、`Slider`、`Switch`、`Picker` |
| 图形和设备 | `Map`、`Camera`、`Svg`、`Chart`、`Qrcode` |

未知元素会产生错误。PascalCase 名称若未由 `Import` 声明，同样被视为未知元素。

### 文本内容

`Text`、`Span` 和 `Label` 推荐使用 `Text` 属性：

```xml
<Text Text="保存成功" />
<Text Text="{Binding message}" />
```

元素中的纯文本也会保留为文本节点，但不建议将可读文本与复杂子节点混用。

## 属性和值

属性值有三种形式：

| 形式 | 示例 | 含义 |
| --- | --- | --- |
| 字面量 | `Width="48px"` | 编译期固定值 |
| 绑定 | `Text="{Binding title}"` | 从当前数据上下文读取 |
| 表达式 | `Style="{Expr { opacity: enabled ? 1 : 0.5 }}"` | 计算 JavaScript 表达式 |

常用属性如下：

| 属性 | 用途 |
| --- | --- |
| `Class` | 一个或多个以空格分隔的类名 |
| `Style` | 内联样式对象或样式文本 |
| `Text` | 文本元素的内容 |
| `Source`、`src` | 图像、视频、动画等资源位置 |
| `Value` | 输入、进度和选择控件的值 |
| `Model` | 双向绑定 |
| `data-*` | 事件数据集 |

属性名会按元素的规则规范化。例如 `data-item-id` 作为数据集字段 `itemId` 使用。受限枚举属性会在编译时检查其取值。

### 事件

原生元素使用事件名作为属性名：

```xml
<Div Click="openDetail" LongPress="remove(item.id)" />
<Input Change="save" />
```

事件值可以是方法名、调用表达式或函数表达式。事件对象自动作为最后一个参数传入。事件方法在当前页面或组件实例上解析。

组件事件使用 `on` 前缀，详见[组件](#组件)。

### 双向绑定

`Model` 的值必须是可赋值的绑定或表达式：

```xml
<Input Model="{Binding name}" />
<Switch Model="{Expr settings.enabled}" />
```

## 绑定和表达式

### Binding

`{Binding path}` 读取当前上下文中的点分路径：

```xml
<Text Text="{Binding user.name}" />
<Image Source="{Binding avatar}" />
```

路径只允许标识符及 `.`。索引、调用或运算应使用 `Expr`。在 `ItemTemplate` 内，绑定从当前列表项读取；`{Binding}` 表示当前项本身。`$item` 和 `$idx` 是列表模板内可用的保留名称。

### Expr

`{Expr expression}` 接受一个 JavaScript 表达式：

```xml
<Text Text="{Expr score + '/' + total}" />
<Div Style="{Expr { backgroundColor: selected ? '#fff' : '#222' }}" />
<Image Source="{Expr images[index]}" />
```

未声明的裸标识符按当前数据上下文解析；模块级 `const` 和函数、标准全局对象以及局部变量保持原样。表达式只能出现在单个属性值中，不能用于元素名、属性名或 XML 结构。

## 条件与列表

### If、ElseIf 和 Else

条件节点必须连续出现：

```xml
<If Test="{Binding loading}">
  <Text Text="加载中" />
</If>
<ElseIf Test="{Binding error}">
  <Text Text="加载失败" />
</ElseIf>
<Else>
  <Text Text="加载完成" />
</Else>
```

`Test` 接受 `Binding` 或 `Expr`。`Else` 不接受 `Test`。

### List

`List` 用 `ItemsSource` 声明集合，并包含唯一的 `ItemTemplate`：

```xml
<List ItemsSource="{Binding items}" Key="id">
  <ItemTemplate>
    <Div Click="select($idx)">
      <Text Text="{Binding name}" />
    </Div>
  </ItemTemplate>
</List>
```

`ItemTemplate` 必须有一个根元素。`Key` 可选，但集合会重排、插入或删除时应提供稳定键。模板内的 `{Binding name}` 等价于读取当前 `$item` 的 `name` 字段；事件仍解析到外层页面或组件的方法。

## 组件

### 声明和导入

组件文件以 `Component` 为根：

```xml
<!-- components/Badge/Badge.wxaml -->
<Component x:Class="Badge">
  <Component.Styles>
    <Style Class="badge"><Setter Property="padding" Value="4px" /></Style>
  </Component.Styles>
  <Div Class="badge"><Text Text="{Binding text}" /></Div>
</Component>
```

使用组件前必须导入：

```xml
<Page x:Class="Home">
  <Import Name="Badge" Source="../../components/Badge/Badge.wxaml" />
  <Badge Text="{Binding status}" onClick="openStatus" />
</Page>
```

`Name` 是模板中使用的组件名称，`Source` 相对当前 `.wxaml` 文件解析。导入可以嵌套；编译器会递归构建所需组件。

### 显式内联

对列表热路径中的纯展示组件，可在导入处请求编译期内联：

```xml
<Import Name="InventorySlot"
        Source="../../components/InventorySlot/InventorySlot.wxaml"
        Inline="true" />
```

内联会把组件模板和样式展开到每个调用点，不生成组件模块或组件实例。它只适用于无状态组件：脚本只能声明 `props`、空的 `data` 对象、普通方法和生命周期钩子，并且只能嵌套导入其他 inline 组件。inline 方法会合并到宿主方法表，生命周期钩子会合并到对应的宿主生命周期；包含状态、普通组件导入或其他模块级逻辑的组件会产生编译错误。移除 `Inline="true"` 即恢复普通组件语义。

### 属性和事件

组件上的普通属性为 Props。WXAML 调用点使用 PascalCase 名称，编译后会转换为运行时的 camelCase Prop 名称；例如 `Text` 对应 `text`。组件 `.js` 的 `props` 数组声明可接受的运行时属性名称：

```js
export default {
  props: ["text", "onClick"],
  data: {}
}
```

组件模板中通过 `{Binding text}` 读取 Prop。调用方使用 `on<Event>` 传递组件事件处理器；在组件实现中，事件名与 `on` 前缀一致。`Model` 是组件调用支持的双向绑定特性，传入的值必须可赋值，编译器将其转换为运行时的 `model` 选项。传递未在 `props` 中声明的普通属性会产生警告。组件样式目前按普通样式规则参与页面样式表；不要依赖自动作用域隔离。

## 样式和媒体查询

WXAML 样式使用 XAML 形式。每个 `Style` 必须且只能使用一个目标属性：`Class`、`Id` 或 `Tag`；每个 `Setter` 需要 `Property` 和 `Value`：

```xml
<Page.Styles>
  <Style Class="card">
    <Setter Property="padding" Value="12px 16px" />
    <Setter Property="border" Value="2px solid #ffffff" />
  </Style>
  <Style Id="primary-panel">
    <Setter Property="padding" Value="12px 16px" />
  </Style>
  <Style Tag="Text">
    <Setter Property="font-size" Value="16px" />
  </Style>
</Page.Styles>
```

`Class` 和 `Id` 的值不含 `.` 或 `#` 前缀；`Tag` 使用元素名称。要复用同一组声明，请写多个 `Style`。盒模型和边框简写会被展开。属性名可使用连字符或驼峰形式。

### 样式资源

可从样式段导入资源字典：

```xml
<Page.Styles>
  <Import Source="../../styles/common.wxaml" />
  <Style Class="page"><Setter Property="padding" Value="16px" /></Style>
</Page.Styles>
```

资源文件的根元素可以是 `ResourceDictionary`、`Styles`、`Page.Styles` 或 `Component.Styles`，内部可继续包含 `Import`、`Style` 和 `Media`。缺失资源或循环导入是编译错误。

### Media

媒体规则使用 `Media` 或 `MediaQuery`：

```xml
<Media Query="(min-width: 320px)">
  <Style Class="page"><Setter Property="padding" Value="24px" /></Style>
</Media>
```

`Query` 可以包含逗号分隔的条件。CSS 文本输入目前在 WXAML 样式段禁用；编译器内部仍保留 CSS 解析能力，恢复该入口前不应在源文件中使用 CSS 块。

## 常量子树

`Const="true"` 是对编译器的断言：该元素及其后代完全不依赖运行时数据。满足条件的节点会按静态节点生成：

```xml
<Stack Const="true">
  <Text Text="固定说明" />
</Stack>
```

常量子树不能包含绑定、表达式、事件、`Model`、条件、列表或自定义组件。违反任一条件会产生错误。`Const` 不应被用于包含用户输入或交互的区域。

## 代码文件

页面和组件使用同名 `.js` 文件，并导出一个对象：

```js
const initialCount = 0;

export default {
  data: {
    count: initialCount
  },
  increment() {
    this.count += 1;
  }
};
```

`data` 声明可绑定状态；组件额外可声明 `props`。模板事件引用的方法必须存在于脚本的可见方法集合中。模块级 `const` 和函数可在模板表达式中使用。相对 JavaScript 导入会随页面或组件一起编译；命名空间导入和重导出不受支持。

## 诊断

编译器将问题分为错误和警告：

- 错误阻止构建，例如 XML 不合法、根元素错误、未知元素、无效绑定、缺失文件、样式导入循环、错误的 `Const` 子树。
- 警告不阻止构建，例如页面中声明 `props`、调用方传入组件未声明的普通 prop。

诊断包含源文件、行和列。修复源文件后应重新执行构建，而不应修改生成目录中的 `.js` 或字节码文件。
