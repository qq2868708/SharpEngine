---
name: engine-code-style
description: Apply the user's engine code writing rules when creating, editing, formatting, or reviewing engine source code. Allow compact one-line function bodies only; expand other block constructs, place braces on separate lines for multiline declarations or bodies, and keep variable declarations, initialization, and assignment outside if conditions.
---

# 引擎代码书写规范

编写、修改或审查引擎代码时，遵守以下规则。规则同样适用于回复、方案和文档中的引擎代码示例。

## 1. 只有一行函数体可以紧凑书写

- 允许整个函数或方法写在一行，例如 `int GetCount() { return CountValue; }`。
- 不得把多个独立语句压在同一行；不得为了使用一行函数体例外，把复杂逻辑或控制结构压成一行。
- 除一行函数体之外，不得把具有主体的结构压成单行，包括类型、命名空间、属性、控制流和对象或集合初始化器；空块也必须展开。
- 属性和访问器不使用一行函数体例外；自动属性、表达式体属性和访问器主体也必须展开。
- `if`、`else`、`for`、`foreach`、`while` 等控制流即使只有一条语句，也必须展开为带大括号的多行主体。
- 普通变量或字段声明、赋值、调用、`return` 等独立声明或语句可以各占一行；无需将它们内部的表达式强行拆成多行。

## 2. 多行声明和主体的大括号独占一行

- 不采用一行函数体写法时，所有块、声明体和函数体的 `{`、`}` 都必须各自独占一行。
- 大括号所在行不得同时出现声明、语句、条件、表达式或其他可执行代码。禁止 `if (...) {`、`} else`、`} catch (...)`、`} while (...);` 等写法。
- 多行函数、构造函数、析构函数、局部函数、匿名函数、访问器，以及类型、命名空间、控制流和初始化器均遵守这条规则。
- 保留语法所需的结束标点，例如 C++ 的 `};`、调用末尾的 `});`；这些标点不视为额外声明或语句，不能借此在大括号同行追加代码。

## 3. if 括号内只写判断条件

- `if (...)` 和 `else if (...)` 的括号内只写判断表达式，不混入局部变量声明、初始化或赋值，也不使用分号或逗号运算符串联多个执行步骤。
- 禁止 C++ 的初始化语句写法，例如 `if (auto* component = FindComponent(); component != nullptr)`；先单独声明并初始化变量，再写 `if` 判断。
- 同样禁止条件内声明并初始化变量，例如 `if (auto* component = FindComponent())`，以及先赋值再判断，例如 `if ((component = FindComponent()) != nullptr)`。
- 多个判断条件仍可通过 `&&`、`||` 等逻辑运算符组合；本规则禁止在条件中混入上述操作，不禁止组合布尔判断。
- 拆分时保持原有求值顺序、短路行为和变量作用域；必要时使用外层代码块或嵌套判断，不将原本有条件执行的初始化提前为无条件执行。

## 示例

允许的一行函数体：

```csharp
int GetCount() { return CountValue; }
```

多行函数、控制流、属性和初始化器：

```csharp
sealed class EngineState
{
    private int CountValue;

    public int Count
    {
        get
        {
            return CountValue;
        }
    }

    public void Update(bool Ready)
    {
        if (Ready)
        {
            CountValue++;
        }
        else
        {
            CountValue = 0;
        }
    }
}
```

初始化器同样展开：

```csharp
var State = new EngineState
{
};
```

先声明和初始化，再判断：

```cpp
auto* component = FindComponent();
if (component != nullptr)
{
    component->Update();
}
```

## 应用与检查

在本次新增或修改的代码及提供的代码示例中落实以上规则；检查是否存在紧凑控制流、单行非函数主体、同一行多个语句、多行结构的大括号与代码同行，或 `if` 条件混入声明、初始化、赋值及多个执行步骤。除非用户要求统一格式化，否则不要批量重排无关代码，也不要因格式调整改变行为。
